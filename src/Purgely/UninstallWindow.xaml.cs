using System.Windows;
using Purgely.Core;

namespace Purgely;

/// <summary>What the user ends up with after the uninstall dialog.</summary>
public enum UninstallOutcome
{
    Cancelled,     // closed before anything was decided
    Removed,       // the official uninstaller finished and the program is gone
    StillListed,   // the uninstaller ended but Windows still lists the program (cancelled, failed, or needs a restart)
    Force,         // the user chose "force uninstall": remove the program's traces with Purgely itself
}

/// <summary>
/// Runs the official uninstaller and shows live progress: steps, elapsed time, whether it waits for an answer
/// in its own window, or whether it seems stuck. Offers a force uninstall when the uninstaller is missing,
/// broken, stuck, or ends without removing the program.
/// </summary>
public partial class UninstallWindow : Window
{
    private const string Done = "", Running = "", Failed = "", Pending = "";

    private readonly InstalledProgram _program;
    private readonly UninstallMonitor _monitor;
    private readonly CancellationTokenSource _cancel = new();
    private bool _finished;

    public UninstallOutcome Outcome { get; private set; } = UninstallOutcome.Cancelled;

    public UninstallWindow(InstalledProgram program)
    {
        InitializeComponent();
        _program = program;
        _monitor = new UninstallMonitor(program);
        TitleText.Text = $"Uninstalling {program.Name}";
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) =>
        {
            if (_finished || Outcome != UninstallOutcome.Cancelled) return;
            // Closing while the uninstaller still runs: it keeps running – say so instead of pretending it stopped.
            if (Dialog.Show(this, "The uninstaller is still running. Purgely will stop watching it, but it keeps " +
                                      "running on its own.\n\nClose anyway?", "Uninstall",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _cancel.Cancel();
        };
    }

    private async Task RunAsync()
    {
        SetStep(1, Running, "Starting the official uninstaller…");
        try
        {
            await Task.Run(_monitor.Start);
            Timeline.Log(TimelineEventType.Uninstalled, _program.Name, "Official uninstaller started from Purgely");
        }
        catch (Exception ex)
        {
            SetStep(1, Failed, "The official uninstaller is missing or broken");
            Finish($"Could not start the uninstaller: {ex.Message}",
                   "The program's own uninstaller doesn't work. A force uninstall removes its files, registry entries, " +
                   "services and startup entries with Purgely instead – you review the list first, and everything can be undone.",
                   force: true, scan: false);
            return;
        }

        var progress = new Progress<UninstallStatus>(ShowStatus);
        UninstallStatus last;
        try { last = await _monitor.WatchAsync(progress, _cancel.Token); }
        catch (OperationCanceledException) { return; }

        SetStep(1, Done, "The official uninstaller has finished");
        SetStep(2, Running, "Checking that the program is gone…");
        await Task.Delay(800); // the registry is sometimes updated a moment after the uninstaller exits
        var stillListed = await Task.Run(() => RegistryPath.Exists(_program.RegistryKey));

        if (!stillListed)
        {
            SetStep(2, Done, "The program is gone");
            SetStep(3, Running, "Next: look for leftovers");
            Outcome = UninstallOutcome.Removed;
            _finished = true;
            ProgressLine.IsIndeterminate = false;
            ProgressLine.Value = 100;
            StatusText.Text = $"Uninstalled in {Format(last.Elapsed)}. Purgely now looks for what it left behind.";
            await Task.Delay(900);
            if (IsVisible) DialogResult = true;
            return;
        }

        SetStep(2, Failed, "Windows still lists the program");
        Finish("The uninstaller ended, but the program is still installed.",
               "It may have been cancelled, failed, or needs a restart to finish. You can look for its parts without " +
               "removing anything, or force the uninstall.",
               force: true, scan: true, scanText: "Scan only (nothing pre-selected)");
        Outcome = UninstallOutcome.StillListed;
    }

    private void ShowStatus(UninstallStatus s)
    {
        ElapsedText.Text = Format(s.Elapsed);
        if (s.Processes == 0)
        {
            StatusText.Text = "Waiting for the uninstaller to finish…";
            return;
        }
        if (s.LooksStuck)
        {
            StatusText.Text = "The uninstaller has done nothing for a while and shows no window.";
            ShowAttention("It looks stuck. You can keep waiting, or stop it and force the uninstall.");
            ForceButton.Visibility = Visibility.Visible;
            return;
        }
        AttentionBar.Visibility = Visibility.Collapsed;
        ForceButton.Visibility = s.Elapsed > TimeSpan.FromMinutes(3) ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = s.HasWindow
            ? "The uninstaller is waiting for you – answer the questions in its window."
            : "Uninstalling in the background (this uninstaller has no window)…";
        SetStep(1, Running, s.HasWindow ? "Official uninstaller – follow its window" : "Official uninstaller – working silently");
    }

    private void Finish(string status, string attention, bool force, bool scan, string scanText = "Scan for leftovers")
    {
        _finished = true;
        ProgressLine.IsIndeterminate = false;
        ProgressLine.Value = 0;
        StatusText.Text = status;
        ShowAttention(attention);
        ForceButton.Visibility = force ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Visibility = scan ? Visibility.Visible : Visibility.Collapsed;
        ScanButtonText.Text = scanText;
        CloseButton.Content = "Close";
    }

    private void ShowAttention(string text)
    {
        AttentionText.Text = text;
        AttentionBar.Visibility = Visibility.Visible;
    }

    private void SetStep(int step, string icon, string text)
    {
        var (i, t) = step switch { 1 => (Step1Icon, Step1Text), 2 => (Step2Icon, Step2Text), _ => (Step3Icon, Step3Text) };
        i.Text = icon;
        i.Opacity = icon == Pending ? 0.5 : 1;
        i.Foreground = icon == Done ? (System.Windows.Media.Brush)FindResource("OkBrush")
                     : icon == Failed ? (System.Windows.Media.Brush)FindResource("AttentionBrush")
                     : (System.Windows.Media.Brush)FindResource("AccentBrush");
        t.Text = text;
        t.Opacity = 1;
    }

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.Seconds} s";

    private void Force_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallMonitor.WindowsInstallerBusy())
        {
            Dialog.Show(this, "Windows Installer is still working (it may be removing or rolling back the program).\n\n" +
                                  "Wait until it has finished, then try again – forcing now could leave a half-removed program.",
                "Force uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var running = _monitor.RunningNames();
        if (Dialog.Show(this,
                $"Force uninstall \"{_program.Name}\"?\n\n" +
                (running.Count > 0 ? $"1. These uninstaller processes are stopped: {string.Join(", ", running.Distinct())}\n"
                                   : "1. The official uninstaller is no longer running.\n") +
                "2. Purgely scans for the program's files, registry entries, services and startup entries.\n" +
                "3. You review the list – nothing is removed before you confirm, and everything goes to an Undo package.\n\n" +
                "Use this only for programs whose own uninstaller doesn't work.",
                "Force uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _cancel.Cancel();
        var stopped = _monitor.StopUninstaller();
        if (stopped > 0) Timeline.Log(TimelineEventType.Uninstalled, _program.Name, $"Uninstaller stopped for a force uninstall ({stopped} process(es))");
        Outcome = UninstallOutcome.Force;
        _finished = true;
        DialogResult = true;
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        Outcome = Outcome == UninstallOutcome.Cancelled ? UninstallOutcome.StillListed : Outcome;
        _finished = true;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
