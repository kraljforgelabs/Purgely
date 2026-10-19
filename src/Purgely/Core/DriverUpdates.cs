using System.Runtime.InteropServices;

namespace Purgely.Core;

/// <summary>A driver update offered by Windows Update for a device in this PC.</summary>
public sealed class DriverUpdate : System.ComponentModel.INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public long SizeBytes { get; init; }
    public bool Optional { get; init; }
    /// <summary>The hardware ID Windows Update matched – used to find the device exactly.</summary>
    public string HardwareId { get; init; } = "";

    public string DateText => DriverDate?.ToString("yyyy-MM-dd") ?? "";
    public string SizeText => SizeBytes > 0 ? Sizes.Format(SizeBytes) : "";
    public string ClassText => DeviceClass.Length > 0 ? DeviceClass : "Device";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }

    private string _status = "";
    private bool _checked = true;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// Driver updates from Windows Update only – the same signed, Microsoft-tested drivers Windows itself installs.
/// Purgely never downloads drivers from third-party sites (a common source of malware and bundleware).
/// Uses the Windows Update Agent (Microsoft.Update.Session).
/// </summary>
public static class DriverUpdates
{
    public static string LastError { get; private set; } = "";

    /// <summary>Asks Windows Update which driver updates exist for this PC. Null when Windows Update can't be reached.</summary>
    public static List<DriverUpdate>? Check()
    {
        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            session.ClientApplicationID = "Purgely";
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            var list = new List<DriverUpdate>();
            foreach (dynamic u in Items(result.Updates))
            {
                DateTime? date = null;
                try { date = (DateTime)u.DriverVerDate; } catch (Exception) { }
                long size = 0;
                try { size = Convert.ToInt64(u.MaxDownloadSize); } catch (Exception) { }
                bool browseOnly = false;
                try { browseOnly = (bool)u.BrowseOnly; } catch (Exception) { }
                var title = (string)u.Title;
                var driverClass = Safe(() => (string)u.DriverClass);
                // Windows Update also ships BIOS/firmware capsules as "drivers" – never offered by Purgely.
                if (Hardware.NeverOfferClasses.Contains(driverClass) || DriverCatalog.IsFirmwareTitle(title, driverClass)) continue;
                list.Add(new DriverUpdate
                {
                    Id = (string)u.Identity.UpdateID,
                    Title = title,
                    HardwareId = Safe(() => (string)u.DriverHardwareID),
                    Manufacturer = Safe(() => (string)u.DriverManufacturer),
                    Model = Safe(() => (string)u.DriverModel),
                    DeviceClass = driverClass,
                    DriverDate = date,
                    SizeBytes = size,
                    Optional = browseOnly,
                });
            }
            LastError = "";
            return list.OrderBy(d => d.DeviceClass).ThenBy(d => d.Title).ToList();
        }
        catch (Exception ex)
        {
            LastError = ex is COMException c ? $"Windows Update error 0x{c.HResult:X8}: {c.Message}" : ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the chosen drivers through Windows Update. Reports (index, status) per driver.
    /// Returns whether Windows asks for a restart.
    /// </summary>
    public static bool Install(IReadOnlyList<DriverUpdate> chosen, IProgress<(int Index, string Status)> progress)
    {
        using var busy = Activity.Begin();
        dynamic session = CreateCom("Microsoft.Update.Session");
        session.ClientApplicationID = "Purgely";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
        var byId = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic u in Items(result.Updates)) byId[(string)u.Identity.UpdateID] = u;

        var reboot = false;
        for (var i = 0; i < chosen.Count; i++)
        {
            if (!byId.TryGetValue(chosen[i].Id, out var update))
            {
                progress.Report((i, "No longer offered by Windows Update"));
                continue;
            }
            try
            {
                if (!(bool)update.EulaAccepted) update.AcceptEula();
                dynamic one = CreateCom("Microsoft.Update.UpdateColl");
                one.Add(update);

                progress.Report((i, "Downloading…"));
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = one;
                dynamic downloaded = downloader.Download();
                if ((int)downloaded.ResultCode is not (2 or 3)) // 2 = succeeded, 3 = succeeded with errors
                {
                    progress.Report((i, $"Download failed (code {(int)downloaded.ResultCode})"));
                    continue;
                }

                progress.Report((i, "Installing…"));
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = one;
                dynamic installed = installer.Install();
                var code = (int)installed.ResultCode;
                var needsReboot = (bool)installed.RebootRequired;
                reboot |= needsReboot;
                progress.Report((i, code is 2 or 3
                    ? needsReboot ? "Installed – restart needed" : "Installed"
                    : $"Install failed (code {code})"));
                Timeline.Log(TimelineEventType.Updated, chosen[i].Title,
                    code is 2 or 3 ? "Driver installed from Windows Update" : $"Driver install failed (code {code})");
            }
            catch (Exception ex)
            {
                progress.Report((i, ex is COMException c ? $"Failed (0x{c.HResult:X8})" : $"Failed: {ex.Message}"));
            }
        }
        return reboot;
    }

    /// <summary>Reads a COM collection by index (works without relying on COM enumeration support).</summary>
    private static List<object> Items(object collection)
    {
        var list = new List<object>();
        var count = (int)collection.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, collection, null)!;
        for (var i = 0; i < count; i++)
            list.Add(collection.GetType().InvokeMember("Item", System.Reflection.BindingFlags.GetProperty, null, collection, new object[] { i })!);
        return list;
    }

    private static string Safe(Func<string> read)
    {
        try { return read() ?? ""; } catch (Exception) { return ""; }
    }

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("Windows Update is not available on this PC.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Update could not be started.");
    }
}
