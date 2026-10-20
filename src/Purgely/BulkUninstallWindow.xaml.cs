using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Purgely.Core;

namespace Purgely;

/// <summary>One program in the bulk-uninstall queue.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public required InstalledProgram Program { get; init; }
    public string Name => Program.Name;
    private string _kind = "";
    public required string Kind { get => _kind; set { _kind = value; Changed(nameof(Kind)); } }
    private string _status = "Waiting";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    private string _icon = ""; // clock
    public string Icon { get => _icon; set { _icon = value; Changed(nameof(Icon)); } }
    private Brush? _iconBrush;
    public Brush? IconBrush { get => _iconBrush; set { _iconBrush = value; Changed(nameof(IconBrush)); } }
    /// <summary>Gone from Windows' list (or a removed program / Store app) – goes into the combined leftover scan.</summary>
    public bool Removed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>
/// Uninstalls several programs one after another (never in parallel – Windows Installer allows only one at a time).
/// Quiet mode where the uninstaller supports it; otherwise the program's own window, followed like the single
/// uninstall. Never kills anything: a stuck or slow uninstaller can only be skipped.
/// </summary>
public partial class BulkUninstallWindow : Window
{
    private readonly List<QueueItem> _queue;
    private CancellationTokenSource? _skip;
    private bool _running, _done, _closeRequested;

    /// <summary>Programs that are gone (or were already removed) – for one combined leftover scan.</summary>
    public List<InstalledProgram> Removed => _queue.Where(q => q.Removed).Select(q => q.Program).ToList();

    public BulkUninstallWindow(IEnumerable<InstalledProgram> programs)
    {
        InitializeComponent();
        _queue = programs.Select(p => new QueueItem
        {
            Program = p,
            Kind = p.IsOrphan ? "Already removed – leftovers only" : p.IsPortable ? "Portable – folder goes to the leftover scan" : p.IsLauncherGame ? $"{p.Launcher} game" : p.IsStoreApp ? "Store app" : p.IsMsi ? "Windows Installer" : "Official uninstaller",
        }).ToList();
        QueueGrid.ItemsSource = _queue;
        TitleText.Text = $"Uninstall {_queue.Count} programs";
        StartText.Text = $"Start ({_queue.Count})";
        StatusText.Text = "Close the programs you are removing and save your work in them first.";
        Closing += (_, e) =>
        {
            if (!_running) return;
            if (Dialog.Show(this, "Programs are still being uninstalled.\n\nStop after the current one? The program being " +
                                  "removed right now will be finished by its own uninstaller.", "Uninstall several programs",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                _closeRequested = true;
            e.Cancel = true;
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_done) { DialogResult = true; return; }
        _running = true;
        StartButton.IsEnabled = false;
        QuietBox.IsEnabled = false;
        CloseButton.Content = "Stop after this one";
        var quiet = QuietBox.IsChecked == true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ElapsedText.Text = $"{(int)clock.Elapsed.TotalMinutes}:{clock.Elapsed.Seconds:00}";
        timer.Start();

        for (var i = 0; i < _queue.Count && !_closeRequested; i++)
        {
            var item = _queue[i];
            Progress.Value = i * 100.0 / _queue.Count;
            QueueGrid.ScrollIntoView(item);
            StatusText.Text = $"{i + 1} of {_queue.Count}: {item.Name}";
            SetState(item, "", null, "Working…");
            try { await UninstallOneAsync(item, quiet); }
            catch (Exception ex) { SetState(item, "", Danger, "Failed: " + ex.Message); }
        }

        timer.Stop();
        _running = false;
        _done = true;
        Progress.Value = 100;
        SkipButton.Visibility = Visibility.Collapsed;
        AttentionBar.Visibility = Visibility.Collapsed;
        var removed = _queue.Count(q => q.Removed);
        var notDone = _queue.Count - removed;
        StatusText.Text = $"Done – {removed} removed" + (notDone > 0 ? $", {notDone} not removed (see Status)" : "") + ".";
        if (removed > 0)
        {
            AttentionBar.Style = (Style)FindResource("InfoBarSuccess");
            AttentionIcon.Style = (Style)FindResource("InfoBarIconSuccess");
            ShowAttention("Next: click \"Scan for leftovers\" – Purgely looks for what all of them left behind and shows it in one list. " +
                          "If you close now, they'll be waiting under \"Clean leftovers\".");
            StartButton.Focus();
        }
        StartButton.IsEnabled = removed > 0;
        StartText.Text = "Scan for leftovers";
        CloseButton.Content = "Close";
        CloseButton.IsEnabled = true;
        if (removed == 0) StartButton.Visibility = Visibility.Collapsed;
    }

    private async Task UninstallOneAsync(QueueItem item, bool quiet)
    {
        var p = item.Program;
        if (p.IsOrphan)
        {
            item.Removed = true;
            SetState(item, "", Ok, "Already removed – its leftovers are included in the scan");
            return;
        }
        if (p.IsPortable)
        {
            item.Removed = true;
            SetState(item, "\uE73E", Ok, "Portable – its folder is offered in the leftover scan");
            return;
        }
        if (p.IsLauncherGame)
        {
            SetState(item, "\uE946", null, $"Skipped – uninstall it in the {p.Launcher} launcher, or on its own with Uninstall");
            return;
        }
        if (p.IsStoreApp)
        {
            var (ok, message) = await Task.Run(() => StoreApps.Remove(p));
            item.Removed = ok;
            SetState(item, ok ? "" : "", ok ? Ok : Danger, ok ? "Removed (reinstall from the Microsoft Store if needed)" : "Failed: " + message);
            return;
        }

        // Windows Installer handles one package at a time – wait for any other install to finish first.
        while (UninstallMonitor.WindowsInstallerBusy())
        {
            item.Status = "Waiting – another installation is running…";
            await Task.Delay(2000);
            if (_closeRequested) { SetState(item, "", null, "Not started (stopped)"); return; }
        }

        var monitor = new UninstallMonitor(p, quiet);
        await Task.Run(monitor.Start);
        if (monitor.QuietMethod.Length > 0) item.Kind = "Quiet: " + monitor.QuietMethod;
        Timeline.Log(TimelineEventType.Uninstalled, p.Name, "Official uninstaller started from Purgely (bulk uninstall)");

        _skip = new CancellationTokenSource();
        SkipButton.Visibility = Visibility.Visible;
        var progress = new Progress<UninstallStatus>(s =>
        {
            if (s.LooksStuck)
            {
                item.Status = "Seems stuck (no window, no activity)";
                ShowAttention($"{p.Name}: the uninstaller isn't doing anything and shows no window. Wait a bit longer, or click \"Skip this one\" – " +
                              "it can be finished later with Uninstall → Force uninstall.");
            }
            else
            {
                AttentionBar.Visibility = s.HasWindow ? Visibility.Visible : Visibility.Collapsed;
                if (s.HasWindow) AttentionText.Text = $"{p.Name}: the uninstaller is waiting for you – answer the questions in its window.";
                item.Status = s.Processes == 0 ? "Finishing…" : s.HasWindow ? "Waiting for you in its window" : "Uninstalling…";
            }
        });
        try { await monitor.WatchAsync(progress, _skip.Token); }
        catch (OperationCanceledException)
        {
            SkipButton.Visibility = Visibility.Collapsed;
            AttentionBar.Visibility = Visibility.Collapsed;
            SetState(item, "", Attention, "Skipped – its uninstaller may still be running");
            return;
        }
        SkipButton.Visibility = Visibility.Collapsed;
        AttentionBar.Visibility = Visibility.Collapsed;

        await Task.Delay(800); // the registry is sometimes updated a moment after the uninstaller exits
        var stillListed = await Task.Run(() => RegistryPath.Exists(p.RegistryKey));
        item.Removed = !stillListed;
        if (stillListed)
            SetState(item, "", Attention, "Still installed – cancelled, failed or needs a restart. Uninstall it on its own (Force uninstall is available there).");
        else
            SetState(item, "", Ok, "Removed");
    }

    private static Brush Ok => (Brush)Application.Current.FindResource("OkBrush");
    private static Brush Danger => (Brush)Application.Current.FindResource("DangerBrush");
    private static Brush Attention => (Brush)Application.Current.FindResource("AttentionBrush");

    private static void SetState(QueueItem item, string icon, Brush? brush, string status)
    {
        item.Icon = icon;
        item.IconBrush = brush;
        item.Status = status;
    }

    private void ShowAttention(string text)
    {
        AttentionText.Text = text;
        AttentionBar.Visibility = Visibility.Visible;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => _skip?.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { _closeRequested = true; CloseButton.IsEnabled = false; CloseButton.Content = "Stopping…"; return; }
        DialogResult = false; // closed without the combined scan – removed programs stay under "Clean leftovers"
    }
}
