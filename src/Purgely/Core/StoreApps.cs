using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>
/// Microsoft Store (AppX/MSIX) apps of the current user. Listed read-only through PowerShell (Get-AppxPackage);
/// removed with Remove-AppxPackage – Windows deletes the app and its data folder itself.
/// Frameworks, system apps and packages Windows marks as non-removable are never listed.
/// </summary>
public static partial class StoreApps
{
    public static List<InstalledProgram> List()
    {
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
'<<<JSON'
@(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' -and -not $_.IsResourcePackage } |
  ForEach-Object { [pscustomobject]@{ Name = $_.Name; Full = $_.PackageFullName; Family = $_.PackageFamilyName;
    Publisher = $_.Publisher; Version = $_.Version.ToString(); Location = $_.InstallLocation; Kind = [string]$_.SignatureKind } }) |
  ConvertTo-Json -Depth 3 -Compress
'JSON>>>'
";
        var (_, stdout, _) = Shell.RunSeparate("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}", 120_000);
        var start = stdout.IndexOf("<<<JSON", StringComparison.Ordinal);
        var end = stdout.LastIndexOf("JSON>>>", StringComparison.Ordinal);
        var result = new List<InstalledProgram>();
        if (start < 0 || end <= start) return result;
        var json = stdout[(start + 7)..end].Trim();
        if (json.Length == 0) return result;

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
                                                                     : new List<JsonElement> { doc.RootElement };
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var location = S("Location");
            var (display, publisher) = ReadManifest(location);
            var name = S("Name");
            result.Add(new InstalledProgram
            {
                Name = Friendly.TryGetValue(name, out var friendly) ? friendly : display.Length > 0 ? display : Prettify(name),
                Publisher = publisher.Length > 0 ? publisher : PublisherFromCertificate(S("Publisher")),
                Version = S("Version"),
                InstallLocation = location,
                RegistryKey = $"appx:{S("Family")}",
                IsStoreApp = true,
                PackageFullName = S("Full"),
                PackageName = name,
                InstallDate = SafeCreation(location),
            });
        }
        return result.GroupBy(p => p.RegistryKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Removes a Store app for the current user. Returns (ok, message).</summary>
    public static (bool Ok, string Message) Remove(InstalledProgram app)
    {
        if (!app.IsStoreApp || !Regex.IsMatch(app.PackageFullName, @"^[\w.\-]+_[\w.\-]+_[\w\-]*_[\w~.\-]*_[a-z0-9]{13}$", RegexOptions.IgnoreCase))
            return (false, "Not a valid Store app package name.");
        using var busy = Activity.Begin();
        // Undo first: a Store app cannot be backed up, so the package records how to get it back from the Store.
        var package = UndoStore.CreatePackage(app.Name);
        UndoStore.SaveActions(package, $"{app.Name} (Store app)", new List<UndoAction>
        {
            new() { Type = "Appx", Original = app.RegistryKey.Replace("appx:", ""), Extra = app.Name },
        });
        var (code, stdout, stderr) = Shell.RunSeparate("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop\"", 300_000);
        if (code != 0)
        {
            FileMover.DeleteBestEffort(package, true); // nothing was removed – the empty Undo package would only confuse
            return (false, LastLine(stderr.Length > 0 ? stderr : stdout));
        }
        Bloatware.RememberRemoved(app);
        Timeline.Log(TimelineEventType.Uninstalled, app.Name, "Store app removed by Purgely – reinstall it from the Microsoft Store if needed");
        return (true, "Removed");
    }

    /// <summary>The Store page of an app, to reinstall it.</summary>
    public static string StoreLink(InstalledProgram app) =>
        $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(app.RegistryKey.Replace("appx:", ""))}";

    private static (string Display, string Publisher) ReadManifest(string location)
    {
        try
        {
            var manifest = Path.Combine(location, "AppxManifest.xml");
            if (!File.Exists(manifest)) return ("", "");
            var xml = File.ReadAllText(manifest);
            var display = Regex.Match(xml, @"<DisplayName>([^<]+)</DisplayName>").Groups[1].Value.Trim();
            var publisher = Regex.Match(xml, @"<PublisherDisplayName>([^<]+)</PublisherDisplayName>").Groups[1].Value.Trim();
            if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = "";
            if (publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = "";
            return (System.Net.WebUtility.HtmlDecode(display), System.Net.WebUtility.HtmlDecode(publisher));
        }
        catch (Exception) { return ("", ""); }
    }

    /// <summary>Names people know, for packages whose manifest name is a resource Purgely can't read.</summary>
    private static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ZuneVideo"] = "Movies & TV",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["MicrosoftWindows.CrossDevice"] = "Phone Link helper (Cross Device)",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.Getstarted"] = "Tips",
        ["Microsoft.BingNews"] = "Microsoft News",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.WindowsAlarms"] = "Clock",
        ["microsoft.windowscommunicationsapps"] = "Mail and Calendar",
    };

    /// <summary>"Microsoft.BingNews" → "Bing News"; "SpotifyAB.SpotifyMusic" → "Spotify Music".</summary>
    private static string Prettify(string name)
    {
        var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
    }

    private static string PublisherFromCertificate(string subject)
    {
        var m = Regex.Match(subject, @"(?:^|,\s*)O=""?([^,""]+)");
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(subject, @"CN=""?([^,""]+)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static DateTime? SafeCreation(string location)
    {
        try { return location.Length > 3 && Directory.Exists(location) ? Directory.GetCreationTime(location) : null; }
        catch (Exception) { return null; }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;
}
