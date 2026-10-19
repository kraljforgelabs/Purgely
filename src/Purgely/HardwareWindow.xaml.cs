using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Purgely.Core;

namespace Purgely;

/// <summary>
/// Hardware and drivers: every device with its driver; a check looks each one up by hardware ID in Windows Update
/// and the Microsoft Update Catalog; installs only verified, matching, Microsoft-signed drivers with Undo.
/// </summary>
public partial class HardwareWindow : Window
{
    private PcSummary? _pc;
    private ObservableCollection<HardwareDevice> _devices = new();
    private ICollectionView? _view;

    /// <summary>Official update apps of graphics/chip makers: (name in Programs, label, official page).</summary>
    private static readonly (string Match, string Label, string Url)[] MakerApps =
    {
        ("NVIDIA app", "NVIDIA app", "https://www.nvidia.com/en-us/software/nvidia-app/"),
        ("GeForce Experience", "NVIDIA GeForce Experience", "https://www.nvidia.com/en-us/software/nvidia-app/"),
        ("AMD Software", "AMD Software (Adrenalin)", "https://www.amd.com/en/support/download/drivers.html"),
        ("Intel® Driver & Support Assistant", "Intel Driver & Support Assistant", "https://www.intel.com/content/www/us/en/support/detect.html"),
        ("Intel Driver & Support Assistant", "Intel Driver & Support Assistant", "https://www.intel.com/content/www/us/en/support/detect.html"),
    };

    public HardwareWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (pc, devices) = await Task.Run(Hardware.Read);
            _pc = pc;
            _devices = new ObservableCollection<HardwareDevice>(devices);
            _view = CollectionViewSource.GetDefaultView(_devices);
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HardwareDevice.Category)));
            _view.Filter = o => o is HardwareDevice d &&
                                (OnlyUpdates.IsChecked != true || d.Update is not null || d.HasProblem) &&
                                (ShowBuiltIn.IsChecked == true || !d.IsInbox || d.IsGeneric || d.HasProblem || d.Update is not null);
            DevicesGrid.ItemsSource = _view;
            PcText.Text = $"{pc.Maker} {pc.Model}  ·  {pc.Cpu}  ·  {pc.Gpu}  ·  {pc.Memory} RAM  ·  BIOS {pc.Bios}";
            // The button names the actual brand ("Acer", "Lenovo", or the motherboard maker on a self-built PC)
            // and is hidden when Purgely doesn't know that brand's official driver page.
            MakerButtonText.Text = $"{FirstWord(pc.DriverMaker)} driver page";
            MakerButton.Visibility = Hardware.MakerSupportUrl(pc) is not null ? Visibility.Visible : Visibility.Collapsed;
            BusyPanel.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
            var problems = devices.Count(d => d.HasProblem);
            SummaryText.Text = $"{devices.Count} devices ({devices.Count(d => d.IsInbox && !d.IsGeneric)} with Windows' built-in drivers, hidden)" +
                               (problems > 0 ? $"  •  {problems} not working" : "");
            await ShowMakerAppsAsync();
        }
        catch (Exception ex)
        {
            BusyText.Text = $"Could not read the hardware list: {ex.Message}";
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private static string FirstWord(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? s;

    private async Task ShowMakerAppsAsync()
    {
        var programs = await Task.Run(ProgramInventory.Load);
        var gpu = _pc?.Gpu ?? "";
        var lines = new List<string>();
        foreach (var vendor in new[] { "NVIDIA", "AMD", "Intel" })
        {
            if (!gpu.Contains(vendor, StringComparison.OrdinalIgnoreCase) && vendor != "Intel") continue;
            var app = MakerApps.FirstOrDefault(a => a.Label.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) &&
                programs.Any(p => p.Name.Contains(a.Match, StringComparison.OrdinalIgnoreCase)));
            if (app.Label is not null) lines.Add($"{app.Label} is installed – it gets {vendor} drivers straight from {vendor}.");
            else if (gpu.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                lines.Add($"For the newest {vendor} graphics driver, {vendor}'s own app is best: {MakerApps.First(a => a.Label.StartsWith(vendor)).Url}");
        }
        MakerAppsText.Text = string.Join("  ", lines);
    }

    // ================================================================== check
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        BusyBar.Visibility = Visibility.Visible;
        DevicesGrid.Opacity = 0.35;
        BusyItem.Text = "";
        BusyCount.Text = "";
        foreach (var d in _devices) { d.Update = null; d.IsChecked = false; d.Status = ""; d.UpdateId = ""; }

        // 1. Windows Update (drivers Windows offers for this PC, including optional ones).
        BusyText.Text = "Asking Windows Update…";
        BusyItem.Text = "Drivers Windows offers for this PC";
        BusyBar.IsIndeterminate = true;
        var wuResult = await Task.Run(DriverUpdates.Check);
        var wuFailed = wuResult is null;
        var windowsUpdate = wuResult ?? new List<DriverUpdate>();
        foreach (var wu in windowsUpdate)
        {
            // Matched by the hardware ID Windows Update itself used - never by a similar-looking name.
            var device = wu.HardwareId.Length > 0
                ? _devices.FirstOrDefault(d => d.Update is null && d.HardwareIds.Contains(wu.HardwareId, StringComparer.OrdinalIgnoreCase))
                : null;
            if (device is null)
            {
                device = new HardwareDevice { Name = wu.Model.Length > 0 ? wu.Model : wu.Title, Class = wu.ClassText, Maker = wu.Manufacturer };
                Hardware.Categorize(device);
                _devices.Add(device);
            }
            device.Source = DriverSource.WindowsUpdate;
            device.UpdateId = wu.Id;
            device.UpdateVersion = wu.DateText;
            device.Update = $"{wu.Title} ({wu.DateText}) – Windows Update{(wu.Optional ? ", optional" : "")}";
            device.IsChecked = !wu.Optional;
        }

        // 2. Microsoft Update Catalog, by hardware ID – maker drivers, not just Microsoft's generic ones.
        var candidates = _devices.Where(d => d.Update is null && d.HardwareIds.Count > 0 && !d.IsDisabled &&
                                             !Hardware.NeverOfferClasses.Contains(d.Class) &&
                                             (d.HasProblem || d.IsGeneric || !d.DriverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))
                                 .ToList();
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = 0;
        BusyBar.Maximum = Math.Max(1, candidates.Count);
        BusyText.Text = "Looking up drivers by hardware ID";
        foreach (var d in candidates) d.Status = "Waiting…";
        var done = 0;
        var failed = 0;
        var upToDate = 0;
        var notListed = 0;
        using var gate = new SemaphoreSlim(3); // be gentle with Microsoft's site
        var tasks = candidates.Select(async device =>
        {
            await gate.WaitAsync();
            device.Status = "Checking…";
            BusyItem.Text = device.Name;
            try
            {
                var lookup = await Task.Run(() => DriverCatalog.FindNewerAsync(device));
                if (lookup.Failed) { Interlocked.Increment(ref failed); device.Status = "Couldn't reach the catalog"; }
                else if (lookup.Driver is null && lookup.Hits == 0) { Interlocked.Increment(ref notListed); device.Status = "Not in the catalog"; }
                else if (lookup.Driver is null)
                {
                    Interlocked.Increment(ref upToDate);
                    device.Status = lookup.NewestSeen.Length > 0 ? $"Up to date (catalog: {lookup.NewestSeen})" : "Up to date";
                }
                else device.Status = "Newer driver found";
                if (lookup.Driver is { } found)
                {
                    device.Source = DriverSource.MicrosoftCatalog;
                    device.UpdateId = found.UpdateId;
                    device.UpdateVersion = found.Version;
                    device.UpdateMatchesExactModel = found.ExactModel;
                    device.UpdateSameFamily = lookup.SameFamily;
                    var notes = new List<string>();
                    if (!lookup.SameFamily)
                        notes.Add(device.DriverVersion.Length == 0 ? "driver for a device without one" : "maker's driver instead of Windows' generic one");
                    if (!found.ExactModel) notes.Add("fits the chip, not checked for your exact model");
                    if (!Hardware.AutoTickClasses.Contains(device.Class))
                        notes.Add(device.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) ? "graphics: the maker's app is better" : "check before installing");
                    device.Update = $"{found.Version} ({found.LastUpdated:yyyy-MM-dd}) – Microsoft Update Catalog" +
                                    (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");
                    // Ticked only when it is a real update for this exact model, for a device that can't stop Windows starting.
                    device.IsChecked = lookup.SameFamily && found.ExactModel && Hardware.AutoTickClasses.Contains(device.Class);
                }
            }
            catch (Exception) { device.Status = "Lookup failed"; /* one device failing must not stop the others */ }
            finally
            {
                gate.Release();
                Dispatcher.Invoke(() =>
                {
                    BusyBar.Value = ++done;
                    BusyCount.Text = $"{done} of {candidates.Count}";
                });
            }
        }).ToList();
        await Task.WhenAll(tasks);

        BusyPanel.Visibility = Visibility.Collapsed;
        DevicesGrid.Opacity = 1;
        CheckButton.IsEnabled = true;
        OnlyUpdates.IsChecked = _devices.Any(d => d.Update is not null || d.HasProblem);
        _view?.Refresh();
        UpdateSummary();
        var newer = _devices.Count(d => d.Update is not null);
        SummaryText.Text = $"Checked {candidates.Count} device(s) and Windows Update: {newer} newer driver(s)  •  {upToDate} up to date" +
                           (notListed > 0 ? $"  •  {notListed} not in the catalog" : "") +
                           (failed > 0 ? $"  •  {failed} couldn't be checked" : "");
        if (wuFailed || failed > 0)
            Dialog.Show(this, (wuFailed ? $"Windows Update could not be reached: {DriverUpdates.LastError}\n" : "") +
                                  (failed > 0 ? $"The Microsoft Update Catalog could not be reached for {failed} device(s).\n" : "") +
                                  "\nThe list may be incomplete - check your internet connection and try again.",
                "Check for newer drivers", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Loose name match between a Windows Update driver model and a device name.</summary>
    private static bool Similar(string a, string b)
    {
        var x = Keywords.Norm(a);
        var y = Keywords.Norm(b);
        return x.Length >= 6 && y.Length >= 6 && (x.Contains(y) || y.Contains(x));
    }

    private void UpdateSummary()
    {
        var found = _devices.Count(d => d.Update is not null);
        var chosen = _devices.Count(d => d.IsChecked && d.Update is not null);
        var problems = _devices.Count(d => d.HasProblem);
        SummaryText.Text = $"{_devices.Count} devices  •  {found} newer driver(s) found  •  {chosen} selected" +
                           (problems > 0 ? $"  •  {problems} not working" : "");
        InstallButton.Content = $"Install selected ({chosen})";
        InstallButton.IsEnabled = chosen > 0;
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();
    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    // ================================================================== install
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _devices.Where(d => d.IsChecked && d.Update is not null).ToList();
        if (chosen.Count == 0) return;
        if (Dialog.Show(this,
                $"Install {chosen.Count} driver(s)?\n\n" +
                string.Join("\n", chosen.Take(12).Select(d => $"• {d.Name}: {d.DriverVersion} → {d.UpdateVersion}")) +
                "\n\nThe screen may flicker or the network may drop for a moment while a driver is replaced. Save your work first.\n" +
                "Each driver from the Catalog is checked (Microsoft signature, lists your device) and your current driver is saved for Undo.",
                "Install drivers", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        using var busy = Purgely.Core.Activity.Begin(); // the page can't close until every driver is done
        CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        // Not IsEnabled = false: the Fluent theme paints a disabled DataGrid white. Dim it and show the busy card instead.
        DevicesGrid.IsHitTestVisible = false;
        DevicesGrid.Opacity = 0.35;
        BusyPanel.Visibility = Visibility.Visible;
        BusyBar.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;
        BusyItem.Text = "";
        BusyCount.Text = "";
        var reboot = false;

        // A restore point first - an extra safety net for drivers (Purgely's Undo is the first one).
        SummaryText.Text = "Creating a restore point…";
        BusyText.Text = "Creating a restore point… (Windows shows no progress for this – usually under a minute)";
        var (rpCode, rpOut) = await Task.Run(() => Shell.Run("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'Purgely driver update' -RestorePointType DEVICE_DRIVER_INSTALL\"",
            300_000));
        var restorePoint = rpCode == 0 && !rpOut.Contains("frequency", StringComparison.OrdinalIgnoreCase);

        // Windows Update drivers in one go.
        var wu = chosen.Where(d => d.Source == DriverSource.WindowsUpdate).ToList();
        if (wu.Count > 0)
        {
            BusyText.Text = $"Installing {wu.Count} driver(s) from Windows Update…";
            SummaryText.Text = BusyText.Text;
            var list = wu.Select(d => new DriverUpdate { Id = d.UpdateId, Title = d.Name }).ToList();
            var progress = new Progress<(int Index, string Status)>(p => wu[p.Index].Status = p.Status);
            try { reboot |= await Task.Run(() => DriverUpdates.Install(list, progress)); }
            catch (Exception ex) { foreach (var d in wu) d.Status = $"Failed: {ex.Message}"; }
        }

        // Catalog drivers one by one.
        var catalog = chosen.Where(d => d.Source == DriverSource.MicrosoftCatalog).ToList();
        for (var i = 0; i < catalog.Count; i++)
        {
            var device = catalog[i];
            BusyText.Text = $"Installing {device.Name}…";
            SummaryText.Text = $"Installing driver {i + 1} of {catalog.Count}…";
            BusyCount.Text = $"{i + 1} of {catalog.Count}";
            var progress = new Progress<string>(s => { device.Status = s; BusyItem.Text = s; });
            try
            {
                var result = await Task.Run(() => DriverCatalog.InstallAsync(device, progress));
                device.Status = result;
                reboot |= result.Contains("restart");
            }
            catch (Exception ex) { device.Status = ex.Message; }
        }

        BusyPanel.Visibility = Visibility.Collapsed;
        DevicesGrid.IsHitTestVisible = true;
        DevicesGrid.Opacity = 1;
        CheckButton.IsEnabled = true;
        var ok = chosen.Count(d => d.Status.StartsWith("Installed"));
        foreach (var d in chosen.Where(d => d.Status.StartsWith("Installed") || d.Status.StartsWith("Added")))
        {
            d.IsChecked = false;
            d.Update = null; // done - not offered again until the next check
        }
        UpdateSummary();
        SummaryText.Text = $"{ok} of {chosen.Count} driver(s) installed" + (reboot ? "  •  restart the PC to finish" : "");
        Dialog.Show(this, $"{ok} of {chosen.Count} driver(s) installed." +
                              (reboot ? "\n\nRestart the PC to finish." : "") +
                              "\n\nIf a device misbehaves:\n" +
                              "• Drivers from the Microsoft Update Catalog: Undo center → Restore.\n" +
                              "• Drivers from Windows Update: Device Manager → the device → Driver → Roll Back Driver.\n" +
                              (restorePoint ? "• A restore point \"Purgely driver update\" was also created."
                                            : "• No new restore point was created (Windows allows one per 24 hours)."),
            "Install drivers", MessageBoxButton.OK, ok == chosen.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ================================================================== helpers
    private void CopySpecs_Click(object sender, RoutedEventArgs e)
    {
        if (_pc is null) return;
        try { Clipboard.SetText(Hardware.SpecSheet(_pc, _devices)); }
        catch (Exception) { Dialog.Show(this, "The clipboard is busy - try again.", "Copy specs"); return; }
        Dialog.Show(this, "The spec sheet is copied.\n\nPaste it into a chat with Claude and ask for newer official drivers – " +
                              "it can search the makers' websites and give you the right links.",
            "Copy specs", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Maker_Click(object sender, RoutedEventArgs e)
    {
        if (_pc is null || Hardware.MakerSupportUrl(_pc) is not { } url) return;
        // Branded PCs: the serial number finds the exact configuration; self-built PCs: the motherboard model.
        var useSerial = _pc.IsBranded && _pc.Serial.Length > 3 && !_pc.Serial.Contains("filled", StringComparison.OrdinalIgnoreCase);
        var key = useSerial ? _pc.Serial : _pc.DriverModel;
        try { Clipboard.SetText(key); } catch (Exception) { }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Dialog.Show(this, $"The {FirstWord(_pc.DriverMaker)} support page is opening.\n\n" +
                              $"Your {(useSerial ? "serial number" : "model")} ({key}) is copied – " +
                              "paste it into the search box there to get the drivers made for exactly this PC.\n\n" +
                              "After downloading, use \"Install downloaded driver…\" so Purgely checks the signature first.",
            "Maker's driver page", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>A driver the user downloaded: run it only when its signature is valid and from the right company.</summary>
    private void InstallFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the driver installer you downloaded",
            Filter = "Driver installers (*.exe;*.msi)|*.exe;*.msi",
        };
        if (dialog.ShowDialog(this) != true) return;
        var sig = Signature.Check(dialog.FileName);
        if (!sig.Trusted)
        {
            Dialog.Show(this, $"Purgely won't run this file.\n\n{sig.Problem}\n\nDownload drivers only from the maker's official website.",
                "Not trusted", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var makers = _devices.Select(d => d.Maker).Append(_pc?.Maker ?? "").Where(m => m.Length > 2).Distinct().ToList();
        var known = makers.Any(m => Signature.SignerMatches(sig.Signer, m));
        if (Dialog.Show(this,
                $"Signed by: {sig.Signer} ✓ (signature valid)\n\n" +
                (known ? "This company makes hardware in this PC.\n\n"
                       : "⚠ This company does not make any device Purgely found in this PC – make sure this is the right driver.\n\n") +
                "Run the installer now?",
                "Install downloaded driver", MessageBoxButton.YesNo, known ? MessageBoxImage.Question : MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Timeline.Log(TimelineEventType.Updated, Path.GetFileName(dialog.FileName), $"Driver installer signed by {sig.Signer} started from Purgely");
        Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
    }
}

/// <summary>A device group opens by itself only when something in it needs a look (technical groups stay closed).</summary>
public sealed class GroupExpandedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var devices = (value as System.Collections.IEnumerable)?.OfType<HardwareDevice>().ToList() ?? new();
        if (devices.Any(d => d.Update is not null || d.HasProblem)) return true;
        return devices.All(d => d.CategoryOrder < 70); // storage, chipset and virtual devices start closed
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>"3 devices · 1 newer driver" next to a group's name.</summary>
public sealed class GroupSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var devices = (value as System.Collections.IEnumerable)?.OfType<HardwareDevice>().ToList() ?? new();
        var parts = new List<string> { $"{devices.Sum(d => d.Count)} device(s)" };
        var updates = devices.Count(d => d.Update is not null);
        if (updates > 0) parts.Add($"{updates} newer driver(s)");
        if (devices.Any(d => d.HasProblem)) parts.Add("needs attention");
        else if (updates == 0) parts.Add("all fine");
        if (devices.Count > 0 && devices[0].CategoryOrder == 80) parts.Add("parts of the motherboard – Windows and Intel keep them current");
        if (devices.Count > 0 && devices[0].CategoryOrder == 90) parts.Add("update or remove the program that installed them");
        return string.Join("  ·  ", parts);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Binding.DoNothing;
}
