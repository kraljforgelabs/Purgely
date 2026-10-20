using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Purgely.Core;
using Purgely.Core.Trial;

namespace Purgely;

public partial class TrialWindow : Window
{
    private readonly Action? _onChanged;
    private TrialRecord? _current;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = new();

    public TrialWindow(Action? onChanged = null)
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        _onChanged = onChanged;
        _clockTimer.Tick += (_, _) => ElapsedText.Text = $"Elapsed {_clock.Elapsed:m\\:ss}";
        Reload();
        // Continue a trial whose installer was started earlier (e.g. before a restart).
        var waiting = TrialEngine.List().FirstOrDefault(t => t.State == TrialState.Installing);
        if (waiting is not null)
        {
            _current = waiting;
            ShowPanel(InstallingPanel);
        }
    }

    private void Reload() => TrialsGrid.ItemsSource = TrialEngine.List();

    private void ShowPanel(FrameworkElement panel)
    {
        if (panel == BusyPanel)
        {
            _clock.Restart();
            ElapsedText.Text = "Elapsed 0:00";
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
            _clock.Stop();
        }
        StartPanel.Visibility = panel == StartPanel ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = panel == BusyPanel ? Visibility.Visible : Visibility.Collapsed;
        InstallingPanel.Visibility = panel == InstallingPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private IProgress<string> Progress() => new Progress<string>(text => BusyText.Text = text);

    private TrialRecord? Selected()
    {
        if (TrialsGrid.SelectedItem is TrialRecord r) return r;
        Dialog.Show(this, "Select a trial in the list first.", "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the installer to try",
            Filter = "Installers (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        // Check before you install: signature, source, installer type, bundled offers. Nothing runs yet.
        ShowPanel(BusyPanel);
        BusyText.Text = "Checking the installer…";
        InstallerCheck.Report check;
        try { check = await Task.Run(() => InstallerCheck.Check(dialog.FileName)); }
        catch (Exception ex)
        {
            ShowPanel(StartPanel);
            Dialog.Show(this, "Could not read the installer.\n\n" + ex.Message, "Check before you install",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShowPanel(StartPanel);
        var answer = Dialog.Show(this,
            $"Check before you install: {System.IO.Path.GetFileName(dialog.FileName)}\n\n" +
            $"Type: {check.Kind}\n" +
            string.Join("\n", check.Findings.Select(f => (f.Warning ? "⚠ " : "• ") + f.Text)) +
            "\n\n\"VirusTotal report\" looks the file up by its fingerprint (SHA-256) – nothing is uploaded.",
            "Check before you install", MessageBoxButton.YesNoCancel,
            check.HasWarnings ? MessageBoxImage.Warning : MessageBoxImage.Information,
            check.HasWarnings ? MessageBoxResult.Cancel : MessageBoxResult.Yes,
            new Dictionary<MessageBoxResult, string>
            {
                [MessageBoxResult.Yes] = "Install and track", [MessageBoxResult.No] = "VirusTotal report",
                [MessageBoxResult.Cancel] = "Cancel",
            });
        if (answer == MessageBoxResult.No && check.Sha256.Length == 64)
        {
            try { Process.Start(new ProcessStartInfo(check.VirusTotalUrl) { UseShellExecute = true }); } catch (Exception) { }
            return;
        }
        if (answer != MessageBoxResult.Yes) return;

        ShowPanel(BusyPanel);
        BusyText.Text = "Preparing…";
        var restorePoint = RestorePointBox.IsChecked == true;
        var progress = Progress();
        try
        {
            _current = await Task.Run(() => TrialEngine.Prepare(dialog.FileName, restorePoint, progress));
        }
        catch (Exception ex)
        {
            ShowPanel(StartPanel);
            Dialog.Show(this, $"Could not prepare the trial:\n\n{ex.Message}", "Trial Mode",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (restorePoint && !_current.RestorePointCreated)
            Dialog.Show(this, "Windows did not create a restore point (System Protection may be off, or one was " +
                                  "already made today). The trial still works – Purgely's own snapshot is ready.",
                "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Information);

        Reload();
        try
        {
            TrialEngine.RunInstaller(_current);
        }
        catch (Exception ex)
        {
            Dialog.Show(this, $"Could not start the installer:\n\n{ex.Message}\n\nYou can start it yourself now.",
                "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShowPanel(InstallingPanel);
    }

    private async void Finished_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        await AnalyzeAsync(_current);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.State != TrialState.Installing)
        {
            Dialog.Show(this, "This trial has already been analyzed.", "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await AnalyzeAsync(r);
    }

    private async Task AnalyzeAsync(TrialRecord r)
    {
        ShowPanel(BusyPanel);
        BusyText.Text = "Analyzing what the installer changed… (this can take a minute or two)";
        var progress = Progress();
        TrialDiff diff;
        try
        {
            diff = await Task.Run(() => TrialEngine.Analyze(r, progress));
        }
        catch (Exception ex)
        {
            ShowPanel(InstallingPanel);
            Dialog.Show(this, $"The analysis failed:\n\n{ex.Message}", "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _current = null;
        ShowPanel(StartPanel);
        Reload();
        _onChanged?.Invoke();

        var message =
            $"Purgely recorded what the installer did:\n\n" +
            $"• Programs registered: {(diff.NewProgramNames.Count == 0 ? "none" : string.Join(", ", diff.NewProgramNames))}\n" +
            $"• New folders: {diff.AddedDirectories.Count}\n" +
            $"• New files (outside new folders): {diff.AddedFiles.Count}\n" +
            $"• Registry: {diff.AddedRegistryKeys.Count} new keys, {diff.ValueChanges.Count + diff.DeletedValues.Count} changed values\n" +
            $"• Services/drivers: {diff.AddedServices.Count}   • Scheduled tasks: {diff.AddedTasks.Count}\n" +
            (diff.ModifiedFiles > 0 ? $"• Existing files changed: {diff.ModifiedFiles} (Revert removes additions; the restore point covers these)\n" : "") +
            (diff.PossibleBundle ? "\n⚠ BUNDLEWARE: the installer registered programs from more than one publisher!\n" : "") +
            "\nNow try the program for as long as you like. Then come back here and choose Keep or Revert.";
        Dialog.Show(this, message, "Trial recorded", MessageBoxButton.OK,
            diff.PossibleBundle ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.State != TrialState.Analyzed)
        {
            Dialog.Show(this, r.State == TrialState.Installing
                    ? "Analyze this trial first (the installer must be finished)."
                    : "This trial is already finished.",
                "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowPanel(BusyPanel);
        BusyText.Text = "Preparing the revert…";
        List<LeftoverItem> items;
        try
        {
            items = await Task.Run(() => TrialEngine.BuildRevertItems(r, ProgramInventory.Load()));
        }
        catch (Exception ex)
        {
            ShowPanel(StartPanel);
            Dialog.Show(this, ex.Message, "Trial Mode", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ShowPanel(StartPanel);

        PageNav.Open(this, new ScanResultsWindow(r.Title, items,
            "Review the changes below. \"Remove selected\" puts your PC back as it was before the trial " +
            "(this itself can be undone in the Undo center).",
            () =>
            {
                TrialEngine.MarkReverted(r);
                Reload();
                _onChanged?.Invoke();
            },
            heading: $"Revert trial: {r.Title}"));
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.State is TrialState.Kept or TrialState.Reverted) return;
        if (Dialog.Show(this, $"Keep \"{r.Title}\" and end the trial?\n\nYou can still uninstall it normally later.",
                "Keep", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        TrialEngine.Keep(r);
        if (_current?.Id == r.Id) { _current = null; ShowPanel(StartPanel); }
        Reload();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { Diff: { } d } r)
        {
            return;
        }
        static string Bullets(IEnumerable<string> items, int max = 12)
        {
            var list = items.ToList();
            return list.Count == 0 ? "  (none)\n" : string.Concat(list.Take(max).Select(i => $"  • {i}\n")) +
                                                    (list.Count > max ? $"  … and {list.Count - max} more\n" : "");
        }
        Dialog.Show(this,
            $"Programs:\n{Bullets(d.NewProgramNames)}\nNew folders:\n{Bullets(d.AddedDirectories)}\n" +
            $"Services:\n{Bullets(d.AddedServices)}\nScheduled tasks:\n{Bullets(d.AddedTasks)}\n" +
            $"New registry keys:\n{Bullets(d.AddedRegistryKeys, 8)}",
            $"Trial details – {r.Title}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.State == TrialState.Analyzed &&
            Dialog.Show(this, "This trial can still be reverted. Delete its record anyway?\n\nAfter this, Revert is no longer possible.",
                "Delete record", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        TrialEngine.Delete(r);
        if (_current?.Id == r.Id) { _current = null; ShowPanel(StartPanel); }
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
