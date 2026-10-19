using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>Where a newer driver for a device can come from.</summary>
public enum DriverSource { None, WindowsUpdate, MicrosoftCatalog, MakerApp }

/// <summary>One device in this PC with its current driver – and, after a check, a newer driver if one was found.</summary>
public sealed class HardwareDevice : INotifyPropertyChanged
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Class { get; init; } = "";
    public string Maker { get; init; } = "";
    public string DriverProvider { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public string InfName { get; init; } = "";
    /// <summary>Hardware IDs, most specific first (PCI\VEN_8086&amp;DEV_A0F0&amp;SUBSYS_…&amp;REV_20 …).</summary>
    public List<string> HardwareIds { get; init; } = new();
    /// <summary>Windows reports a problem (no driver, error code).</summary>
    public int ProblemCode { get; init; }

    /// <summary>Not working (22 = disabled by the user and 45 = not connected are not problems).</summary>
    public bool HasProblem => ProblemCode is not (0 or 22 or 45);

    /// <summary>How many identical devices this row stands for (e.g. two identical controllers).</summary>
    public int Count { get; set; } = 1;
    public string DisplayName => Count > 1 ? $"{Name}  ×{Count}" : Name;

    /// <summary>Plain-language group ("Sound", "Chipset and motherboard parts"…), see <see cref="Hardware.Categorize"/>.</summary>
    public string Category { get; set; } = "";
    public int CategoryOrder { get; set; }

    /// <summary>A virtual device that a program installed (DAEMON Tools, VPN adapters…), not real hardware.</summary>
    public bool IsVirtual { get; set; }
    public bool IsDisabled => ProblemCode == 22;
    /// <summary>A generic Microsoft driver on hardware made by someone else – the maker's driver may add features.</summary>
    public bool IsGeneric => DriverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                             Maker.Length > 0 && !Maker.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                             !Maker.StartsWith("(", StringComparison.Ordinal);
    /// <summary>
    /// Windows' own built-in driver (Microsoft, version 10.0.x; all of them carry the date 2006-06-21 on purpose).
    /// Updated with Windows itself – never "old".
    /// </summary>
    public bool IsInbox => DriverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                           (DriverVersion.StartsWith("10.0.") || DriverDate is { Year: 2006, Month: 6, Day: 21 });
    public double AgeYears => DriverDate is { } d ? (DateTime.Now - d).TotalDays / 365.25 : 0;

    public string DriverDateText => DriverDate?.ToString("yyyy-MM-dd") ?? "";
    public string StateText => HasProblem ? (ProblemCode == 28 ? "No driver" : "Not working")
        : IsDisabled ? "Disabled"
        : Update is not null ? "Update found"
        : IsGeneric ? "Generic driver"
        : IsVirtual && AgeYears >= 4 ? "Old – from a program"
        : IsVirtual ? "From a program"
        : IsInbox ? "Windows built-in"
        : AgeYears >= 4 ? "Old driver"
        : "OK";

    // ---- result of a check
    private string? _update;
    private DriverSource _source;
    private bool _checked;
    private string _status = "";

    /// <summary>Text of the newer driver, e.g. "31.0.101.5592 (2024-05-02) – Microsoft Update Catalog".</summary>
    public string? Update { get => _update; set { _update = value; Changed(nameof(Update)); Changed(nameof(StateText)); } }
    public DriverSource Source { get => _source; set { _source = value; Changed(nameof(Source)); } }
    public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    /// <summary>What to install: a Windows Update id or a catalog update id.</summary>
    public string UpdateId { get; set; } = "";
    public string UpdateVersion { get; set; } = "";
    public bool UpdateMatchesExactModel { get; set; }
    /// <summary>The newer driver comes from the same maker as the current one (a real update, not an alternative).</summary>
    public bool UpdateSameFamily { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>The PC itself: model, BIOS, CPU, graphics, memory – for the header and for "Copy specs".</summary>
public sealed record PcSummary(string Maker, string Model, string Bios, string Cpu, string Gpu, string Memory, string Windows, string Serial,
                               string BoardMaker = "", string BoardModel = "")
{
    /// <summary>Self-built PCs report placeholders like "System manufacturer" – then the motherboard maker is what counts.</summary>
    public bool IsBranded => Maker.Length > 0 &&
        !System.Text.RegularExpressions.Regex.IsMatch(Maker, "system manufacturer|to be filled|default string|o\\.e\\.m|not applicable|^$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Who supplies the drivers: the laptop/PC brand, or the motherboard maker for self-built PCs.</summary>
    public string DriverMaker => IsBranded ? Maker : BoardMaker;
    public string DriverModel => IsBranded ? Model : BoardModel;
}

/// <summary>Reads the devices and drivers of this PC (through Windows' own CIM/WMI data, read-only).</summary>
public static class Hardware
{
    /// <summary>Device classes worth checking for driver updates (others are Windows' own plumbing).</summary>
    public static readonly HashSet<string> InterestingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "MEDIA", "AudioEndpoint", "Bluetooth", "HIDClass", "Mouse", "Keyboard", "Camera", "Image",
        "Biometric", "System", "USB", "SCSIAdapter", "HDC", "SDHost", "Monitor", "Printer", "Sensor", "SoftwareComponent",
        "Extension", "MTD", "Firmware",
    };

    /// <summary>
    /// Never offered from the Catalog: boot disk controllers (a wrong driver = PC won't start, and then Undo can't
    /// run), firmware, and driver add-ons. These come from the PC maker.
    /// </summary>
    public static readonly HashSet<string> NeverOfferClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCSIAdapter", "HDC", "Firmware", "Extension", "SoftwareComponent",
    };

    /// <summary>The only classes whose driver may be ticked automatically – "leaf" devices that can't stop Windows starting.</summary>
    public static readonly HashSet<string> AutoTickClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Net", "Bluetooth", "MEDIA", "Camera", "Image", "Biometric", "Printer", "Monitor",
    };

    public static (PcSummary Pc, List<HardwareDevice> Devices) Read()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$cs = Get-CimInstance Win32_ComputerSystem
$board = Get-CimInstance Win32_BaseBoard
$bios = Get-CimInstance Win32_BIOS
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$gpu = (Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }) -join ' + '
$os = Get-CimInstance Win32_OperatingSystem
$signed = @{}
Get-CimInstance Win32_PnPSignedDriver | ForEach-Object { if ($_.DeviceID) { $signed[$_.DeviceID] = $_ } }
$devices = Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -and $_.Present -ne $false -and $_.ConfigManagerErrorCode -ne 45 } | ForEach-Object {
  $d = $signed[$_.PNPDeviceID]
  [pscustomobject]@{
    Id = $_.PNPDeviceID; Name = $_.Name; Class = $_.PNPClass; Maker = $_.Manufacturer
    Error = [int]$_.ConfigManagerErrorCode; Ids = @($_.HardwareID)
    Provider = $d.DriverProviderName; Version = $d.DriverVersion; Date = $d.DriverDate; Inf = $d.InfName
  }
}
'<<<JSON'
[pscustomobject]@{
  Maker = $cs.Manufacturer; Model = $cs.Model; Bios = ""$($bios.Manufacturer) $($bios.SMBIOSBIOSVersion)""
  Serial = $bios.SerialNumber; Cpu = $cpu; Gpu = $gpu
  BoardMaker = $board.Manufacturer; BoardModel = $board.Product
  Memory = ""$([math]::Round($cs.TotalPhysicalMemory / 1GB)) GB""; Windows = ""$($os.Caption) $($os.Version)""
  Devices = @($devices)
} | ConvertTo-Json -Depth 4 -Compress
'JSON>>>'
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var (_, stdout, stderr) = Shell.RunSeparate("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", 120_000);
        var start = stdout.IndexOf("<<<JSON", StringComparison.Ordinal);
        var end = stdout.LastIndexOf("JSON>>>", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Could not read the hardware list: {(stderr.Length > 0 ? stderr : stdout).Trim()}");
        var json = stdout[(start + 7)..end].Trim();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var pc = new PcSummary(S(root, "Maker"), S(root, "Model"), S(root, "Bios"), S(root, "Cpu"), S(root, "Gpu"),
                               S(root, "Memory"), S(root, "Windows"), S(root, "Serial"), S(root, "BoardMaker"), S(root, "BoardModel"));

        var devices = new List<HardwareDevice>();
        if (root.TryGetProperty("Devices", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var ids = new List<string>();
                if (e.TryGetProperty("Ids", out var idList) && idList.ValueKind == JsonValueKind.Array)
                    ids.AddRange(idList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                var error = e.TryGetProperty("Error", out var err) && err.ValueKind == JsonValueKind.Number ? err.GetInt32() : 0;
                devices.Add(new HardwareDevice
                {
                    DeviceId = S(e, "Id"), Name = S(e, "Name"), Class = S(e, "Class"), Maker = S(e, "Maker"),
                    DriverProvider = S(e, "Provider"), DriverVersion = S(e, "Version"), DriverDate = ParseCimDate(e),
                    InfName = S(e, "Inf"), HardwareIds = ids, ProblemCode = error,
                });
            }
        }
        foreach (var d in devices) Categorize(d);
        // Identical devices (same name, class and driver) are shown once with "×2".
        devices = devices
            .GroupBy(d => $"{d.Class}|{d.Name}|{d.DriverVersion}|{d.ProblemCode}", StringComparer.OrdinalIgnoreCase)
            .Select(g => { var first = g.First(); first.Count = g.Count(); return first; })
            .ToList();
        // Real hardware only: skip software devices without hardware IDs and Windows' virtual plumbing.
        var useful = devices
            .Where(d => d.Name.Length > 0 && (d.HasProblem || InterestingClasses.Contains(d.Class)))
            .Where(d => d.HardwareIds.Count > 0 && !d.DeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) ||
                        d.HasProblem)
            .OrderBy(d => d.CategoryOrder).ThenByDescending(d => d.HasProblem).ThenBy(d => d.Name)
            .ToList();
        return (pc, useful);
    }

    private static readonly string[] VirtualMakers =
    {
        "disc soft", "daemon", "sysprogs", "wincdemu", "tap-windows", "openvpn", "wireguard", "vmware", "virtualbox", "oracle",
        "hyper-v", "nordvpn", "expressvpn", "cloudflare", "tailscale", "zerotier", "npcap", "parsec", "sunshine", "virtual audio",
        "voicemeeter", "vb-audio", "obs", "steam streaming", "nefarius", "vigem",
    };

    /// <summary>Puts a device into a group a normal person understands, with technical parts collected together.</summary>
    public static void Categorize(HardwareDevice d)
    {
        var text = $"{d.Maker} {d.Name} {d.DriverProvider}".ToLowerInvariant();
        // Windows' own virtual parts (Hyper-V, Wi-Fi Direct…) are built in, not "installed by a program".
        var microsoft = d.DriverProvider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        d.Maker.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        d.IsVirtual = !microsoft && (VirtualMakers.Any(v => text.Contains(v)) ||
                      (text.Contains("virtual") && !text.Contains("intel") && !text.Contains("nvidia") && !text.Contains("realtek")));
        (d.Category, d.CategoryOrder) = d switch
        {
            { HasProblem: true } => ("Not working – needs attention", 0),
            { IsVirtual: true } => ("Virtual devices (installed by programs)", 90),
            _ => d.Class.ToUpperInvariant() switch
            {
                "DISPLAY" => ("Graphics", 10),
                "MEDIA" or "AUDIOENDPOINT" => ("Sound", 20),
                "NET" => ("Network (Wi-Fi, Ethernet)", 30),
                "BLUETOOTH" => ("Bluetooth", 40),
                "CAMERA" or "IMAGE" => ("Camera", 50),
                "BIOMETRIC" => ("Fingerprint and face sign-in", 55),
                "HIDCLASS" or "MOUSE" or "KEYBOARD" => ("Keyboard, touchpad and mouse", 60),
                "MONITOR" => ("Screens", 65),
                "PRINTER" => ("Printers", 66),
                "SCSIADAPTER" or "HDC" => ("Storage (disk controllers)", 70),
                _ => ("Chipset and motherboard parts", 80),
            },
        };
    }

    /// <summary>CIM dates arrive either as "/Date(1700000000000)/" or as "20240502000000.000000-000".</summary>
    private static DateTime? ParseCimDate(JsonElement e)
    {
        if (!e.TryGetProperty("Date", out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            var ms = Regex.Match(s, @"Date\((\d+)");
            if (ms.Success) return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(ms.Groups[1].Value)).LocalDateTime;
            if (s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(s, out var any)) return any;
        }
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(inner.GetString(), out var dt)) return dt;
        return null;
    }

    /// <summary>A plain-text spec sheet to paste into a chat with an assistant (no serial number unless asked).</summary>
    public static string SpecSheet(PcSummary pc, IEnumerable<HardwareDevice> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PC: {pc.Maker} {pc.Model}");
        if (!pc.IsBranded) sb.AppendLine($"Motherboard: {pc.BoardMaker} {pc.BoardModel}");
        sb.AppendLine($"BIOS: {pc.Bios}");
        sb.AppendLine($"Windows: {pc.Windows}");
        sb.AppendLine($"CPU: {pc.Cpu}");
        sb.AppendLine($"Graphics: {pc.Gpu}");
        sb.AppendLine($"Memory: {pc.Memory}");
        sb.AppendLine();
        sb.AppendLine("Devices (class | name | maker | driver version | driver date | first hardware ID | state):");
        foreach (var d in devices)
            sb.AppendLine($"- {d.Class} | {d.Name} | {d.Maker} | {d.DriverVersion} | {d.DriverDateText} | {d.HardwareIds.FirstOrDefault()} | {d.StateText}");
        sb.AppendLine();
        sb.AppendLine("Please find newer official drivers (maker's own website or Microsoft Update Catalog) for these devices.");
        return sb.ToString();
    }

    /// <summary>The PC maker's official driver page (laptop makers tailor drivers to each model).</summary>
    public static string? MakerSupportUrl(PcSummary pc)
    {
        var m = pc.DriverMaker.ToLowerInvariant();
        var model = Uri.EscapeDataString(pc.DriverModel);
        if (m.Contains("acer")) return "https://www.acer.com/us-en/support/drivers-and-manuals";
        if (m.Contains("lenovo")) return "https://pcsupport.lenovo.com/";
        if (m.Contains("dell")) return "https://www.dell.com/support/home/";
        if (m.Contains("hp") || m.Contains("hewlett")) return "https://support.hp.com/drivers";
        if (m.Contains("asus")) return $"https://www.asus.com/support/search-result/?keyword={model}";
        if (m.Contains("msi") || m.Contains("micro-star")) return $"https://www.msi.com/search/{model}";
        if (m.Contains("gigabyte")) return "https://www.gigabyte.com/Support";
        if (m.Contains("asrock")) return "https://www.asrock.com/support/index.asp";
        if (m.Contains("razer")) return "https://mysupport.razer.com/";
        if (m.Contains("medion")) return "https://www.medion.com/gb/service/start/";
        if (m.Contains("huawei")) return "https://consumer.huawei.com/en/support/";
        if (m.Contains("toshiba") || m.Contains("dynabook")) return "https://support.dynabook.com/drivers";
        if (m.Contains("fujitsu")) return "https://support.ts.fujitsu.com/";
        if (m.Contains("lg ")) return "https://www.lg.com/us/support/software-firmware-drivers";
        if (m.Contains("samsung")) return "https://www.samsung.com/support/";
        if (m.Contains("microsoft")) return "https://support.microsoft.com/surface/download-drivers-and-firmware-for-surface";
        return null;
    }
}
