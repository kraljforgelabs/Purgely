using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>One browser extension (read-only view – removing is done in the browser itself, so sync can't bring it back).</summary>
public sealed class BrowserExtension
{
    public required string Browser { get; init; }
    public required string Profile { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public bool FromStore { get; init; } = true;
    public bool Enabled { get; init; } = true;
    /// <summary>Plain-language summary of the risky permissions ("" = nothing special).</summary>
    public string Access { get; init; } = "";
    public bool IsRisky => !FromStore || Access.Length > 0;
    public string Group => $"{Browser} · {Profile}";
    public string SourceText => FromStore ? "Store" : "Outside the store";
    public string StateText => Enabled ? "On" : "Off";
    public string Verdict => !FromStore
        ? "Check – not installed from the browser's store. Remove it if you don't know it."
        : Access.Length > 0 ? "Check – " + Access : "OK";
    internal string ManageUrl { get; init; } = "";
    internal string StoreUrl { get; init; } = "";
    internal string Exe { get; init; } = "";
}

/// <summary>Lists the extensions of Chrome, Edge, Brave, Vivaldi and Firefox for the current user and flags risky ones.</summary>
public static class BrowserExtensions
{
    private sealed record Chromium(string Browser, string UserData, string Exe, string Manage, string StoreUrl, string[] StoreUpdateHosts);

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly Chromium[] Chromiums =
    {
        new("Google Chrome", Path.Combine(Local, @"Google\Chrome\User Data"), "chrome.exe", "chrome://extensions",
            "https://chromewebstore.google.com/detail/{0}", new[] { "clients2.google.com" }),
        new("Microsoft Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"), "msedge.exe", "edge://extensions",
            "https://microsoftedge.microsoft.com/addons/detail/{0}", new[] { "edge.microsoft.com", "clients2.google.com" }),
        new("Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), "brave.exe", "brave://extensions",
            "https://chromewebstore.google.com/detail/{0}", new[] { "clients2.google.com", "go-updater.brave.com" }),
        new("Vivaldi", Path.Combine(Local, @"Vivaldi\User Data"), "vivaldi.exe", "vivaldi://extensions",
            "https://chromewebstore.google.com/detail/{0}", new[] { "clients2.google.com" }),
    };

    /// <summary>Permissions that let an extension see or change a lot – shown in plain words.</summary>
    private static readonly (string Permission, string Words)[] Risky =
    {
        ("<all_urls>", "can read and change every website"),
        ("*://*/*", "can read and change every website"),
        ("http://*/*", "can read and change every website"),
        ("https://*/*", "can read and change every website"),
        ("webRequest", "can watch your web traffic"),
        ("nativeMessaging", "can talk to programs on your PC"),
        ("debugger", "can control open pages"),
        ("proxy", "can redirect your traffic"),
        ("management", "can manage other extensions"),
        ("cookies", "can read your cookies"),
        ("history", "can read your browsing history"),
        ("clipboardRead", "can read your clipboard"),
    };

    public static List<BrowserExtension> List()
    {
        var result = new List<BrowserExtension>();
        foreach (var browser in Chromiums)
        {
            try { result.AddRange(ReadChromium(browser)); } catch (Exception) { }
        }
        try { result.AddRange(ReadFirefox()); } catch (Exception) { }
        return result.OrderBy(e => e.Group).ThenByDescending(e => e.IsRisky).ThenBy(e => e.Name).ToList();
    }

    private static IEnumerable<BrowserExtension> ReadChromium(Chromium b)
    {
        if (!Directory.Exists(b.UserData)) yield break;
        foreach (var profileDir in Directory.EnumerateDirectories(b.UserData))
        {
            var folder = Path.GetFileName(profileDir);
            if (folder != "Default" && !folder.StartsWith("Profile ", StringComparison.Ordinal)) continue;
            var extensions = Path.Combine(profileDir, "Extensions");
            if (!Directory.Exists(extensions)) continue;
            var profileName = ProfileName(b.UserData, folder);
            foreach (var idDir in Directory.EnumerateDirectories(extensions))
            {
                var id = Path.GetFileName(idDir);
                if (id.Length != 32 || id == "Temp") continue;
                var versionDir = Directory.EnumerateDirectories(idDir).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
                if (versionDir is null) continue;
                BrowserExtension? ext = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(versionDir, "manifest.json")),
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var m = doc.RootElement;
                    if (m.TryGetProperty("theme", out _)) continue; // themes are harmless
                    var name = Localize(Str(m, "name"), versionDir, Str(m, "default_locale"));
                    var update = Str(m, "update_url");
                    var fromStore = update.Length == 0 || b.StoreUpdateHosts.Any(h => update.Contains(h, StringComparison.OrdinalIgnoreCase));
                    ext = new BrowserExtension
                    {
                        Browser = b.Browser, Profile = profileName, Id = id, Name = name.Length > 0 ? name : id,
                        Version = Str(m, "version"), FromStore = fromStore, Access = AccessOf(m),
                        ManageUrl = b.Manage, StoreUrl = string.Format(b.StoreUrl, id), Exe = b.Exe,
                    };
                }
                catch (Exception) { }
                if (ext is not null) yield return ext;
            }
        }
    }

    private static string ProfileName(string userData, string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userData, "Local State")));
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache) &&
                cache.TryGetProperty(folder, out var info) && info.TryGetProperty("name", out var n))
                return n.GetString() ?? folder;
        }
        catch (Exception) { }
        return folder;
    }

    private static IEnumerable<BrowserExtension> ReadFirefox()
    {
        var profiles = Path.Combine(Roaming, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) yield break;
        foreach (var profileDir in Directory.EnumerateDirectories(profiles))
        {
            var file = Path.Combine(profileDir, "extensions.json");
            if (!File.Exists(file)) continue;
            List<BrowserExtension> found = new();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("addons", out var addons)) continue;
                foreach (var a in addons.EnumerateArray())
                {
                    if (Str(a, "type") != "extension") continue;
                    var location = Str(a, "location");
                    if (location.StartsWith("app-", StringComparison.Ordinal)) continue; // built into Firefox
                    var name = a.TryGetProperty("defaultLocale", out var loc) ? Str(loc, "name") : "";
                    var id = Str(a, "id");
                    var access = "";
                    if (a.TryGetProperty("userPermissions", out var perms))
                        access = Describe(Strings(perms, "permissions").Concat(Strings(perms, "origins")));
                    found.Add(new BrowserExtension
                    {
                        Browser = "Mozilla Firefox", Profile = Path.GetFileName(profileDir).Split('.').Last(), Id = id,
                        Name = name.Length > 0 ? name : id, Version = Str(a, "version"),
                        FromStore = !(a.TryGetProperty("sourceURI", out var src) && src.ValueKind == JsonValueKind.String &&
                                      !(src.GetString() ?? "").Contains("addons.mozilla.org", StringComparison.OrdinalIgnoreCase) &&
                                      !(src.GetString() ?? "").Contains("mozilla.net", StringComparison.OrdinalIgnoreCase)),
                        Enabled = a.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
                        Access = access, ManageUrl = "about:addons", Exe = "firefox.exe",
                        StoreUrl = $"https://addons.mozilla.org/firefox/addon/{Uri.EscapeDataString(id)}/",
                    });
                }
            }
            catch (Exception) { }
            foreach (var f in found) yield return f;
        }
    }

    private static string AccessOf(JsonElement manifest) =>
        Describe(Strings(manifest, "permissions").Concat(Strings(manifest, "host_permissions"))
            .Concat(manifest.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.EnumerateArray().SelectMany(c => Strings(c, "matches")) : Enumerable.Empty<string>()));

    private static string Describe(IEnumerable<string> permissions)
    {
        var set = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", Risky.Where(r => set.Contains(r.Permission)).Select(r => r.Words).Distinct());
    }

    private static IEnumerable<string> Strings(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
            : Enumerable.Empty<string>();

    private static string Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>"__MSG_appName__" → the English (or default) text from _locales.</summary>
    private static string Localize(string text, string folder, string defaultLocale)
    {
        if (!text.StartsWith("__MSG_", StringComparison.Ordinal) || !text.EndsWith("__", StringComparison.Ordinal)) return text;
        var key = text[6..^2];
        foreach (var locale in new[] { "en", "en_US", "en_GB", defaultLocale }.Where(l => l.Length > 0).Distinct())
        {
            try
            {
                var file = Path.Combine(folder, "_locales", locale, "messages.json");
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? "";
            }
            catch (Exception) { }
        }
        return "";
    }

    /// <summary>Opens the browser's own extension page, where the user removes or turns off extensions.</summary>
    public static void OpenManager(BrowserExtension e) =>
        Process.Start(new ProcessStartInfo(e.Exe, e.ManageUrl) { UseShellExecute = true });

    /// <summary>Opens the extension's store page (to read what it is and its reviews).</summary>
    public static void OpenStorePage(BrowserExtension e) =>
        Process.Start(new ProcessStartInfo(e.StoreUrl) { UseShellExecute = true });
}
