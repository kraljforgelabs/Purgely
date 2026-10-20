using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>A device that isn't working, with what the problem means in plain words.</summary>
public sealed record DeviceProblem(HardwareDevice Device, string Meaning, string Advice, bool CanRestart, bool CanReinstall,
                                   bool CanEnable, bool NeedsDriver);

/// <summary>A device Windows remembers but that isn't connected any more (an old USB stick, a mouse, a phone).</summary>
public sealed class GhostDevice : System.ComponentModel.INotifyPropertyChanged
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public string Class { get; init; } = "";
    public string Maker { get; init; } = "";
    private string _status = "";
    private bool _isChecked;
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public bool IsChecked { get => _isChecked; set { _isChecked = value; Changed(nameof(IsChecked)); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>A Microsoft runtime many programs and games need.</summary>
public sealed record RuntimeCheck(string Name, string WingetId, bool Installed, string Why);

/// <summary>What the network test found.</summary>
public sealed record NetworkDiagnosis(bool Adapter, bool Gateway, bool Internet, bool Dns, string Summary);

/// <summary>
/// Repair tools: devices that don't work, devices Windows still remembers, no sound, no internet, missing runtimes.
/// Everything uses Windows' own tools (pnputil, ipconfig, netsh, winget) – nothing is downloaded from third-party sites.
/// </summary>
public static partial class Repair
{
    // ------------------------------------------------------------------ device problems
    /// <summary>Classes Purgely never removes or reinstalls: a wrong move there can stop Windows from starting.</summary>
    private static readonly HashSet<string> CriticalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Processor", "Computer", "SCSIAdapter", "HDC", "Volume", "VolumeSnapshot", "DiskDrive", "Firmware",
        "SecurityDevices", "SoftwareComponent", "Extension", "Display", "Battery", "ACPI",
    };

    /// <summary>Device Manager problem codes in plain words.</summary>
    public static (string Meaning, string Advice) Explain(int code) => code switch
    {
        1 => ("Windows has no proper settings for this device.", "Reinstall the device, or look for a driver."),
        3 => ("The driver is damaged, or the PC is short on memory.", "Restart the device; if that doesn't help, reinstall it."),
        10 => ("The device can't start.", "Restart the device. If it keeps failing, look for a newer driver."),
        12 => ("The device can't get the resources it needs (conflict with another device).", "Restart the PC; if the problem continues, check the PC maker's drivers."),
        14 => ("The device won't work properly until you restart the PC.", "Restart the PC."),
        18 or 24 => ("The driver needs to be installed again.", "Reinstall the device."),
        19 => ("The device's settings in the registry are damaged.", "Reinstall the device."),
        21 => ("Windows is removing this device.", "Restart the PC."),
        22 => ("The device is turned off (disabled).", "Turn it back on if you need it."),
        28 => ("No driver is installed.", "Look for a driver (Driver updates)."),
        29 => ("The device is turned off in the BIOS/UEFI.", "Turn it on in the PC's BIOS/UEFI settings if you need it."),
        31 => ("Windows can't load the driver for this device.", "Reinstall the device, or look for a newer driver."),
        32 => ("The driver's service is turned off.", "Reinstall the device."),
        37 or 39 or 41 => ("The driver couldn't be loaded – it may be damaged or missing.", "Reinstall the device, or look for a newer driver."),
        43 => ("Windows stopped the device because it reported a problem.", "Restart the device; for USB devices, try another port."),
        48 => ("The driver was blocked because it is known to cause problems.", "Look for a newer driver from the maker."),
        52 => ("The driver's signature can't be checked.", "Look for a signed driver (Driver updates)."),
        _ => ($"Windows reports problem code {code}.", "Restart the device; if the problem continues, look for a newer driver."),
    };

    /// <summary>USB controllers and root hubs carry the keyboard, the mouse – sometimes even the boot disk.</summary>
    private static bool IsCritical(HardwareDevice d) =>
        CriticalClasses.Contains(d.Class) ||
        d.Class.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
        (d.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || d.DeviceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase));

    public static List<DeviceProblem> FindProblems(IEnumerable<HardwareDevice> devices) =>
        devices.Where(d => d.HasProblem || d.IsDisabled)
               .Select(d =>
               {
                   var (meaning, advice) = Explain(d.ProblemCode);
                   var safe = !IsCritical(d) && d.DeviceId.Length > 0;
                   return new DeviceProblem(d, meaning, advice,
                       CanRestart: safe && d.ProblemCode is not (22 or 28 or 29),
                       CanReinstall: safe && d.ProblemCode is not (22 or 29),
                       CanEnable: d.ProblemCode == 22 && d.DeviceId.Length > 0,
                       NeedsDriver: d.ProblemCode is 28 or 31 or 37 or 39 or 41 or 48 or 52);
               })
               .ToList();

    /// <summary>Restarts one device (like unplugging and plugging it in). Nothing is removed.</summary>
    public static (bool Ok, string Message) RestartDevice(HardwareDevice device) =>
        PnpUtil($"/restart-device \"{Quote(device.DeviceId)}\"", "Device restarted.");

    public static (bool Ok, string Message) EnableDevice(HardwareDevice device)
    {
        var result = PnpUtil($"/enable-device \"{Quote(device.DeviceId)}\"", "Device turned on.");
        if (result.Ok) Timeline.Log(TimelineEventType.SettingChanged, device.Name, "Device turned on (Repair)");
        return result;
    }

    /// <summary>
    /// Removes the device and lets Windows find it again with its driver – what "Uninstall device" + "Scan for hardware
    /// changes" do in Device Manager. The driver package stays installed. Never for critical classes.
    /// </summary>
    public static (bool Ok, string Message) ReinstallDevice(HardwareDevice device)
    {
        if (IsCritical(device)) return (false, "Purgely doesn't reinstall this kind of device – it could stop Windows from starting.");
        using var busy = Activity.Begin();
        var removed = PnpUtil($"/remove-device \"{Quote(device.DeviceId)}\"", "");
        if (!removed.Ok) return removed;
        var scan = PnpUtil("/scan-devices", "");
        Timeline.Log(TimelineEventType.SettingChanged, device.Name, "Device reinstalled (Repair)");
        return scan.Ok ? (true, "Reinstalled – Windows found the device again.")
                       : (true, "Removed – Windows will find it again when you restart the PC or plug it in.");
    }

    // ------------------------------------------------------------------ devices that aren't connected
    /// <summary>Only "leaf" devices that Windows sets up again by itself when they are plugged in.</summary>
    private static readonly HashSet<string> GhostClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Not Bluetooth or printers: those must be paired/added again by hand, so "plug it in again" isn't true for them.
        "USB", "HIDClass", "Mouse", "Keyboard", "WPD", "Image", "Camera", "Ports",
        "Monitor", "AudioEndpoint", "MEDIA", "Net", "Biometric", "SmartCardReader", "AndroidUsbDeviceClass", "Modem",
        "Sensor", "USBDevice",
    };

    /// <summary>Devices Windows remembers but that are not connected now (read-only; language-independent).</summary>
    public static List<GhostDevice> FindGhosts()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
'<<<JSON'
@(Get-PnpDevice | Where-Object { -not $_.Present } | ForEach-Object {
  [pscustomobject]@{ Id = $_.InstanceId; Name = $_.FriendlyName; Class = $_.Class; Maker = $_.Manufacturer }
}) | ConvertTo-Json -Depth 2 -Compress
'JSON>>>'
";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var (_, stdout, stderr) = Shell.RunSeparate("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", 120_000);
        var start = stdout.IndexOf("<<<JSON", StringComparison.Ordinal);
        var end = stdout.LastIndexOf("JSON>>>", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Windows couldn't list disconnected devices: " + (stderr.Length > 0 ? stderr : stdout).Trim());
        var json = stdout[(start + 7)..end].Trim();
        var list = new List<GhostDevice>();
        if (json.Length == 0) return list;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList() : new List<System.Text.Json.JsonElement> { doc.RootElement };
        static string S(System.Text.Json.JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var e in items)
        {
            var id = S(e, "Id");
            var cls = S(e, "Class");
            if (id.Length == 0 || !GhostClasses.Contains(cls)) continue;
            // Software-created and root-enumerated entries are Windows' own plumbing, not unplugged hardware.
            if (id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) && !cls.Equals("WPD", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || id.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) || id.Contains("BTHPAN", StringComparison.OrdinalIgnoreCase)) continue; // paired devices
            if (id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase)) continue;
            var name = S(e, "Name");
            var maker = S(e, "Maker");
            // Windows' own virtual network parts (RAS, WAN Miniport, Teredo…) – not hardware someone unplugged.
            if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase) && maker.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new GhostDevice { InstanceId = id, Name = name.Length > 0 ? name : id, Class = cls, Maker = maker });
        }
        return list.OrderBy(g => g.Class).ThenBy(g => g.Name).ToList();
    }

    /// <summary>Forgets one disconnected device. If it is plugged in again, Windows sets it up again by itself.</summary>
    public static (bool Ok, string Message) RemoveGhost(GhostDevice ghost)
    {
        if (!GhostClasses.Contains(ghost.Class)) return (false, "Not a removable kind of device.");
        var result = PnpUtil($"/remove-device \"{Quote(ghost.InstanceId)}\"", "Forgotten");
        if (result.Ok) Timeline.Log(TimelineEventType.Cleaned, ghost.Name, "Disconnected device forgotten (Repair) – plug it in to set it up again");
        return result;
    }

    // ------------------------------------------------------------------ sound
    /// <summary>Restarts Windows' audio services – fixes "no sound" after sleep or a driver hiccup in most cases.</summary>
    public static (bool Ok, string Message) RestartAudio()
    {
        // -Force stops the services that depend on Audio Endpoint Builder too (Windows Audio, the sound card maker's
        // service) – remember which were running and start all of them again.
        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$running = @((Get-Service AudioEndpointBuilder).DependentServices | Where-Object Status -eq 'Running' | ForEach-Object Name); " +
            "Restart-Service -Name AudioEndpointBuilder -Force; " +
            "foreach ($s in @('Audiosrv') + $running) { Start-Service -Name $s -ErrorAction SilentlyContinue }; " +
            "if ((Get-Service Audiosrv).Status -ne 'Running') { throw 'Windows Audio did not start' }";
        var (code, output) = Shell.Run("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))}", 90_000);
        return code == 0 ? (true, "Windows Audio was restarted. Try playing a sound now.")
                         : (false, "Windows Audio couldn't be restarted: " + LastLine(output));
    }

    // ------------------------------------------------------------------ network
    /// <summary>Finds where the connection breaks: adapter → router → internet → names (DNS). Read-only; run off the UI thread.</summary>
    public static async Task<NetworkDiagnosis> DiagnoseNetworkAsync()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();
        var gateway = adapters.SelectMany(a => a.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                                        !a.Equals(IPAddress.Any));
        static async Task<bool> PingAsync(IPAddress address)
        {
            try
            {
                using var ping = new Ping();
                for (var i = 0; i < 2; i++)
                    if ((await ping.SendPingAsync(address, 1500)).Status == IPStatus.Success) return true;
            }
            catch (Exception) { }
            return false;
        }
        var hasAdapter = adapters.Count > 0;
        var gw = gateway is not null && await PingAsync(gateway);
        bool dns;
        try { dns = (await Dns.GetHostAddressesAsync("www.msftconnecttest.com")).Length > 0; }
        catch (Exception) { dns = false; }
        // Windows' own connectivity check (works where ping is blocked); then plain addresses, in case only DNS fails.
        var internet = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            internet = (await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt")).Contains("Microsoft Connect Test");
        }
        catch (Exception) { }
        if (!internet) internet = await PingAsync(IPAddress.Parse("1.1.1.1")) || await PingAsync(IPAddress.Parse("8.8.8.8"));

        var summary = !hasAdapter ? "No network connection is active. Check that Wi-Fi is on (or the cable is plugged in) and Airplane mode is off."
            : internet && dns ? "The internet works: your PC can reach the internet and look up website names."
            : internet ? "The internet works, but website names can't be looked up (DNS). \"Refresh the connection\" usually fixes it."
            : gateway is null ? "Your PC didn't get an address from the router. Try \"Refresh the connection\" first; if that doesn't help, restart the router."
            : gw ? "Your PC can reach the router, but not the internet. Restart the router; if that doesn't help, the problem is at your internet provider."
            : "Your PC can't reach the router. Try \"Refresh the connection\" first; if that doesn't help, restart the router.";
        return new NetworkDiagnosis(hasAdapter, gw, internet, dns, summary);
    }

    /// <summary>
    /// Clears the DNS cache and asks the router for a new address. All connections drop for a few seconds (VPN and
    /// remote desktop sessions too). The result is judged by a new test, not by ipconfig's exit code – that is non-zero
    /// whenever any adapter is unplugged.
    /// </summary>
    public static async Task<string> RefreshConnectionAsync()
    {
        await Task.Run(() =>
        {
            Shell.Run("ipconfig.exe", "/flushdns", 30_000);
            Shell.Run("ipconfig.exe", "/release", 120_000);
            Shell.Run("ipconfig.exe", "/renew", 300_000);
            Shell.Run("ipconfig.exe", "/flushdns", 30_000);
        });
        var after = await Task.Run(() => DiagnoseNetworkAsync());
        return "Refreshed. " + after.Summary;
    }

    /// <summary>
    /// Resets Windows' network settings (Winsock and TCP/IP) to their defaults – the last resort before calling the
    /// provider. Clears fixed (static) IP and DNS settings; they are saved to a text file first. Needs a restart.
    /// </summary>
    public static (bool Ok, string Message) ResetNetwork()
    {
        using var busy = Activity.Begin();
        // Backup first: the current addresses and the full netsh configuration, so fixed settings can be typed in again.
        string? backup = null;
        try
        {
            Directory.CreateDirectory(DataFolders.Shared);
            backup = Path.Combine(DataFolders.Shared, $"network-settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var (_, config) = Shell.Run("netsh.exe", "interface ip show config", 60_000);
            var (_, dump) = Shell.Run("netsh.exe", "-c interface dump", 60_000);
            File.WriteAllText(backup, "Network settings before Purgely's reset\r\n\r\n" + config + "\r\n\r\n--- netsh dump ---\r\n" + dump);
        }
        catch (Exception) { backup = null; }
        if (backup is null) return (false, "The current settings couldn't be saved first, so nothing was reset.");

        var (c1, o1) = Shell.Run("netsh.exe", "winsock reset", 60_000);
        var (c2, o2) = Shell.Run("netsh.exe", "int ip reset", 60_000);
        if (c1 != 0 && c2 != 0) return (false, "The reset didn't work: " + LastLine(o1 + "\n" + o2));
        Timeline.Log(TimelineEventType.SettingChanged, "Network", "Network settings reset (Repair) – restart needed");
        return (true, "Network settings were reset. Restart the PC to finish. Your previous settings were saved to " + backup +
                      " – if you had fixed (static) addresses, type them in again from there. A VPN program may need to be set up again.");
    }

    // ------------------------------------------------------------------ runtimes
    private static readonly (string Name, string WingetId, string Why, Func<IReadOnlyList<InstalledProgram>, bool> IsInstalled)[] Runtimes =
    {
        ("Visual C++ Redistributable 2015–2022 (64-bit)", "Microsoft.VCRedist.2015+.x64",
         "Most programs and games need it (\"MSVCP140.dll / VCRUNTIME140.dll is missing\").",
         p => VcRuntimePresent(Environment.SystemDirectory) ||
              p.Any(x => Regex.IsMatch(x.Name, @"Visual C\+\+ (20(15|17|19|22)|v14).*\b(x64|64-bit)\b", RegexOptions.IgnoreCase))),
        ("Visual C++ Redistributable 2015–2022 (32-bit)", "Microsoft.VCRedist.2015+.x86",
         "Older and 32-bit programs and games need it.",
         p => VcRuntimePresent(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)) ||
              p.Any(x => Regex.IsMatch(x.Name, @"Visual C\+\+ (20(15|17|19|22)|v14).*\b(x86|32-bit)\b", RegexOptions.IgnoreCase))),
        ("DirectX End-User Runtime (older games)", "Microsoft.DirectX",
         "Older games ask for it (\"d3dx9_43.dll / xinput1_3.dll is missing\"). Windows 11 includes the newer DirectX.",
         _ => File.Exists(Path.Combine(Environment.SystemDirectory, "d3dx9_43.dll")) && File.Exists(Path.Combine(Environment.SystemDirectory, "xinput1_3.dll"))),
        (".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8",
         "Many newer Windows programs are built on it.",
         p => p.Any(x => Regex.IsMatch(x.Name, @"(Windows|\.NET)\s+Desktop Runtime.*\b8\.", RegexOptions.IgnoreCase)) ||
              Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")) &&
              Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"), "8.*").Any()),
    };

    /// <summary>
    /// The Visual C++ 2015–2022 runtime is there when its DLLs are (the program's name changed over the years:
    /// "2015-2022 Redistributable", now "v14 Redistributable").
    /// </summary>
    private static bool VcRuntimePresent(string systemFolder) =>
        systemFolder.Length > 0 &&
        new[] { "vcruntime140.dll", "msvcp140.dll", "vcruntime140_1.dll" }
            .Take(Environment.Is64BitOperatingSystem && systemFolder.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase) ? 3 : 2)
            .All(f => File.Exists(Path.Combine(systemFolder, f)));

    public static List<RuntimeCheck> CheckRuntimes(IReadOnlyList<InstalledProgram> programs) =>
        Runtimes.Select(r => new RuntimeCheck(r.Name, r.WingetId, r.IsInstalled(programs), r.Why)).ToList();

    /// <summary>Installs a Microsoft runtime with Windows Package Manager (official source, hidden, no questions).</summary>
    public static (bool Ok, string Message) InstallRuntime(RuntimeCheck runtime)
    {
        if (!Runtimes.Any(r => r.WingetId == runtime.WingetId)) return (false, "Unknown package.");
        var winget = WingetUpdates.FindWinget();
        if (winget is null) return (false, "Windows Package Manager (winget) is not installed – get \"App Installer\" from the Microsoft Store.");
        using var busy = Activity.Begin();
        var (code, output) = Shell.Run(winget,
            $"install --id \"{runtime.WingetId}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            900_000);
        // 0 = installed; -1978335189 (0x8A15002B) = already installed / no newer version.
        // -1978335135 (0x8A150061) = already installed.
        var ok = code is 0 or -1978335189 or -1978335135 or 3010;
        return ok ? (true, code == 3010 ? "Installed – restart the PC to finish." : "Installed.")
                  : (false, "Couldn't install: " + LastLine(output));
    }

    // ------------------------------------------------------------------ helpers
    private static (bool Ok, string Message) PnpUtil(string args, string success)
    {
        var (code, output) = Shell.Run("pnputil.exe", args, 120_000);
        // 3010 = done, restart needed.
        return code is 0 or 3010
            ? (true, code == 3010 ? (success.Length > 0 ? success + " " : "") + "Restart the PC to finish." : success)
            : (false, LastLine(output));
    }

    /// <summary>Instance IDs never contain quotes; anything odd is refused rather than passed on.</summary>
    private static string Quote(string id) =>
        id.Length == 0 || id.EndsWith('\\') || id.Any(c => c == '"' || char.IsControl(c)) ? throw new ArgumentException("Unexpected device ID") : id;

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";
}
