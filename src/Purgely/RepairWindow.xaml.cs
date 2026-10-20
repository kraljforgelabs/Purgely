using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Purgely.Core;

namespace Purgely;

/// <summary>One row of "Devices that don't work".</summary>
public sealed class ProblemRow : INotifyPropertyChanged
{
    public required DeviceProblem Problem { get; init; }
    public string Name => Problem.Device.DisplayName;
    public string Meaning => Problem.Meaning;
    public string Advice => "What helps: " + Problem.Advice;
    public Visibility RestartVisibility => Problem.CanRestart ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReinstallVisibility => Problem.CanReinstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EnableVisibility => Problem.CanEnable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DriverVisibility => Problem.NeedsDriver ? Visibility.Visible : Visibility.Collapsed;
    private string _status = "";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row of "missing DLL" runtimes.</summary>
public sealed class RuntimeRow : INotifyPropertyChanged
{
    public required RuntimeCheck Runtime { get; init; }
    public string Name => Runtime.Name;
    public string Why => Runtime.Why;
    private bool _installed;
    private string _status = "";
    public bool Installed { get => _installed; set { _installed = value; Changed(nameof(InstallVisibility)); } }
    public Visibility InstallVisibility => Installed ? Visibility.Collapsed : Visibility.Visible;
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Repair: devices that don't work, devices Windows remembers, sound, internet, missing runtimes.</summary>
public partial class RepairWindow : Window
{
    private readonly ObservableCollection<ProblemRow> _problems = new();
    private readonly ObservableCollection<GhostDevice> _ghosts = new();
    private readonly ObservableCollection<RuntimeRow> _runtimes = new();

    public RepairWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        ProblemsList.ItemsSource = _problems;
        GhostsGrid.ItemsSource = _ghosts;
        RuntimesList.ItemsSource = _runtimes;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (problems, runtimes) = await Task.Run(() =>
            {
                var (_, devices) = Hardware.Read();
                var p = Repair.FindProblems(devices);
                var r = Repair.CheckRuntimes(ProgramInventory.Load());
                return (p, r);
            });
            foreach (var p in problems) _problems.Add(new ProblemRow { Problem = p });
            ProblemsStatus.Text = problems.Count == 0
                ? "All devices work – Windows reports no problems."
                : $"{problems.Count} device(s) need attention.";
            foreach (var r in runtimes)
                _runtimes.Add(new RuntimeRow { Runtime = r, Installed = r.Installed, Status = r.Installed ? "Installed" : "Not installed" });
        }
        catch (Exception ex)
        {
            ProblemsStatus.Text = "Couldn't read the device list: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ devices that don't work
    private async Task RunOnRowAsync(object sender, string busyText, Func<HardwareDevice, (bool Ok, string Message)> action)
    {
        if ((sender as FrameworkElement)?.Tag is not ProblemRow row || sender is not Button button) return;
        button.IsEnabled = false;
        row.Status = busyText;
        try
        {
            var (ok, message) = await Task.Run(() => action(row.Problem.Device));
            row.Status = (ok ? message : "Didn't work: " + message) +
                         (row.Problem.Device.Count > 1 ? $" (Windows lists {row.Problem.Device.Count} identical devices – this changed the first one; repeat for the others in Device Manager if needed.)" : "");
        }
        catch (Exception ex) { row.Status = "Didn't work: " + ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private async void RestartDevice_Click(object sender, RoutedEventArgs e) =>
        await RunOnRowAsync(sender, "Restarting the device…", Repair.RestartDevice);

    private async void EnableDevice_Click(object sender, RoutedEventArgs e) =>
        await RunOnRowAsync(sender, "Turning it on…", Repair.EnableDevice);

    private async void ReinstallDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProblemRow row) return;
        if (Dialog.Show(this,
                $"Reinstall \"{row.Name}\"?\n\nWindows removes the device and finds it again straight away, with its driver – " +
                "like \"Uninstall device\" and \"Scan for hardware changes\" in Device Manager. The driver itself stays installed. " +
                "The device may stop working for a few seconds.",
                "Reinstall device", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                new Dictionary<MessageBoxResult, string> { [MessageBoxResult.Yes] = "Reinstall", [MessageBoxResult.No] = "Cancel" })
            != MessageBoxResult.Yes) return;
        await RunOnRowAsync(sender, "Reinstalling…", Repair.ReinstallDevice);
    }

    private void FindDriver_Click(object sender, RoutedEventArgs e) => PageNav.Open(this, new HardwareWindow());

    // ------------------------------------------------------------------ devices that aren't connected
    private async void FindGhosts_Click(object sender, RoutedEventArgs e)
    {
        FindGhostsButton.IsEnabled = false;
        GhostsStatus.Visibility = Visibility.Visible;
        GhostsStatus.Text = "Asking Windows which devices it remembers…";
        try
        {
            var ghosts = await Task.Run(Repair.FindGhosts);
            _ghosts.Clear();
            foreach (var g in ghosts) _ghosts.Add(g);
            var any = ghosts.Count > 0;
            GhostsCard.Visibility = SelectAllGhostsButton.Visibility = ForgetGhostsButton.Visibility =
                any ? Visibility.Visible : Visibility.Collapsed;
            GhostsStatus.Text = any
                ? $"{ghosts.Count} device(s) aren't connected. Tick the ones you no longer use."
                : "Nothing to tidy – Windows remembers no unplugged devices of the kinds Purgely handles.";
            UpdateForgetButton();
        }
        catch (Exception ex) { GhostsStatus.Text = "Couldn't list them: " + ex.Message; }
        finally { FindGhostsButton.IsEnabled = true; FindGhostsButton.Content = "Search again"; }
    }

    private void GhostCheck_Click(object sender, RoutedEventArgs e) => UpdateForgetButton();

    private void SelectAllGhosts_Click(object sender, RoutedEventArgs e)
    {
        var all = _ghosts.Any(g => !g.IsChecked);
        foreach (var g in _ghosts) g.IsChecked = all && g.Status.Length == 0;
        SelectAllGhostsButton.Content = all ? "Untick all" : "Tick all";
        UpdateForgetButton();
    }

    private void UpdateForgetButton()
    {
        var n = _ghosts.Count(g => g.IsChecked);
        ForgetGhostsButton.Content = $"Forget ticked ({n})";
        ForgetGhostsButton.IsEnabled = n > 0;
    }

    private async void ForgetGhosts_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _ghosts.Where(g => g.IsChecked).ToList();
        if (ticked.Count == 0) return;
        if (Dialog.Show(this,
                $"Forget {ticked.Count} device(s)?\n\nWindows removes them from its list. This can't be undone in the Undo center, " +
                "but nothing is lost: when you plug a forgotten device in again, Windows sets it up again by itself.",
                "Devices that aren't connected", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                new Dictionary<MessageBoxResult, string> { [MessageBoxResult.Yes] = "Forget", [MessageBoxResult.No] = "Cancel" })
            != MessageBoxResult.Yes) return;

        ForgetGhostsButton.IsEnabled = FindGhostsButton.IsEnabled = SelectAllGhostsButton.IsEnabled = false;
        var done = 0;
        try
        {
            using (Core.Activity.Begin())
            {
                foreach (var g in ticked)
                {
                    GhostsStatus.Text = $"Forgetting {g.Name}… ({done + 1} of {ticked.Count})";
                    var (ok, message) = await Task.Run(() => Repair.RemoveGhost(g));
                    g.Status = ok ? "Forgotten" : "Failed: " + message;
                    g.IsChecked = false;
                    if (ok) done++;
                }
            }
            GhostsStatus.Text = $"Forgot {done} of {ticked.Count} device(s).";
        }
        finally
        {
            FindGhostsButton.IsEnabled = SelectAllGhostsButton.IsEnabled = true;
            UpdateForgetButton();
        }
    }

    // ------------------------------------------------------------------ sound
    private async void RestartAudio_Click(object sender, RoutedEventArgs e)
    {
        AudioButton.IsEnabled = false;
        AudioStatus.Text = "Restarting Windows Audio…";
        try
        {
            var (_, message) = await Task.Run(Repair.RestartAudio);
            AudioStatus.Text = message;
        }
        finally { AudioButton.IsEnabled = true; }
    }

    private void SoundSettings_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:sound");

    // ------------------------------------------------------------------ internet
    private void SetNetBusy(bool busy) => TestNetButton.IsEnabled = RefreshNetButton.IsEnabled = ResetNetButton.IsEnabled = !busy;

    private async void TestNetwork_Click(object sender, RoutedEventArgs e)
    {
        SetNetBusy(true);
        NetStatus.Text = "Testing: network adapter → router → internet → website names…";
        try { NetStatus.Text = (await Task.Run(() => Repair.DiagnoseNetworkAsync())).Summary; }
        catch (Exception ex) { NetStatus.Text = "The test failed: " + ex.Message; }
        finally { SetNetBusy(false); }
    }

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e)
    {
        SetNetBusy(true);
        NetStatus.Text = "Refreshing the connection – all connections drop for a few seconds, VPN and remote desktop too…";
        try { NetStatus.Text = await Repair.RefreshConnectionAsync(); }
        catch (Exception ex) { NetStatus.Text = "Didn't work: " + ex.Message; }
        finally { SetNetBusy(false); }
    }

    private async void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (Dialog.Show(this,
                "Reset Windows' network settings?\n\nThis puts Winsock and TCP/IP back to Windows' defaults – the last thing to try " +
                "before calling your internet provider. Wi-Fi passwords stay.\n\n" +
                "Fixed (static) IP and DNS settings are cleared – Purgely saves them to a text file first so you can type them in again. " +
                "VPN programs may need to be set up again. You need to restart the PC afterwards.",
                "Reset network settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
                new Dictionary<MessageBoxResult, string> { [MessageBoxResult.Yes] = "Reset", [MessageBoxResult.No] = "Cancel" })
            != MessageBoxResult.Yes) return;
        SetNetBusy(true);
        NetStatus.Text = "Resetting network settings…";
        try
        {
            var (_, message) = await Task.Run(Repair.ResetNetwork);
            NetStatus.Text = message;
        }
        finally { SetNetBusy(false); }
    }

    // ------------------------------------------------------------------ runtimes
    private async void InstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RuntimeRow row || sender is not Button button) return;
        button.IsEnabled = false;
        row.Status = "Installing with Windows Package Manager… (this can take a minute)";
        try
        {
            var (ok, message) = await Task.Run(() => Repair.InstallRuntime(row.Runtime));
            row.Status = ok ? message : "Didn't work: " + message;
            if (ok) row.Installed = true;
        }
        catch (Exception ex) { row.Status = "Didn't work: " + ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }
}
