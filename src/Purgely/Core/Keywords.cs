using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>Name normalization and the word lists that keep matching safe.</summary>
public static partial class Keywords
{
    /// <summary>Words too generic to identify a program on their own.</summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "program", "programs", "software", "update", "updater", "setup",
        "installer", "install", "driver", "drivers", "runtime", "service", "services", "tools",
        "tool", "system", "common", "files", "data", "the", "and", "for", "free", "app", "apps",
        "application", "client", "plugin", "plugins", "edition", "version", "professional", "pro",
        "home", "standard", "player", "manager", "helper", "launcher", "support", "package",
        "redistributable", "library", "framework", "components", "component", "desktop", "online",
        "studio", "suite", "pack", "bit", "sdk", "net", "core", "web", "host", "media", "video",
        "audio", "game", "games", "user", "users", "local", "machine", "browser", "center",
        "control", "panel", "security", "cloud", "drive", "office", "english", "language", "uninstaller",
    };

    /// <summary>Normalized folder/key names that must never be offered for removal.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "windowsnt", "commonfiles", "windowsapps", "modifiablewindowsapps",
        "microsoftnet", "packages", "temp", "tmp", "programs", "internetexplorer", "windowsdefender",
        "windowsdefenderadvancedthreatprotection", "windowsmail", "windowsmediaplayer",
        "windowsphotoviewer", "windowspowershell", "windowssidebar", "windowssecurity", "packagecache",
        "installer", "classes", "policies", "clients", "wow6432node", "registeredapplications", "odbc",
        "intel", "amd", "nvidia", "nvidiacorporation", "realtek", "startmenu", "startup", "desktop",
        "documents", "appdata", "local", "roaming", "locallow", "systemprofile", "users", "public",
        "default", "programdata", "programfiles", "programfilesx86", "dotnet", "msbuild",
        "referenceassemblies", "uninstallinformation", "appv", "comms", "connecteddevicesplatform",
        "crashdumps", "d3dscache", "history", "inetcache", "microsoftedge", "administrativetools",
        "accessibility", "accessories", "maintenance", "windowstools", "systemtools", "ssh",
        "drivers", "system32", "syswow64", "winsxs", "fonts", "boot", "recovery", "perflogs",
        "onedrive", "google", "mozilla", "adobe", "oracle", "java", "python", "steam", "steamapps",
        "purgely", "packagedcom", "vssadmin", "wbem",
    };

    public static string Norm(string? s) => NonAlnum().Replace((s ?? "").ToLowerInvariant(), "");

    public static bool IsGeneric(string normalized) =>
        StopWords.Contains(normalized) || Protected.Contains(normalized);

    /// <summary>"Notepad++ 8.6 (64-bit x64)" → "notepad".</summary>
    public static string CleanName(string? s)
    {
        s = (s ?? "").ToLowerInvariant();
        s = Brackets().Replace(s, " ");
        s = VersionNumber().Replace(s, " ");
        s = Architecture().Replace(s, " ");
        s = NonAlnumRun().Replace(s, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"Adobe Systems Inc." → "adobe".</summary>
    public static string CleanPublisher(string? s)
    {
        s = (s ?? "").ToLowerInvariant()
            .Replace("®", " ").Replace("™", " ").Replace("(r)", " ").Replace("(tm)", " ").Replace("(c)", " ");
        return Norm(CompanySuffix().Replace(s, " "));
    }

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonAlnumRun();
    [GeneratedRegex(@"\(.*?\)|\[.*?\]")] private static partial Regex Brackets();
    [GeneratedRegex(@"\bv?\d+(\.\d+)*\b")] private static partial Regex VersionNumber();
    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|bit)\b")] private static partial Regex Architecture();
    [GeneratedRegex(@"\b(inc|incorporated|ltd|llc|gmbh|corp|corporation|co|company|limited|ag|sa|bv|srl|doo|software|technologies|technology|systems|group|computer|computers|electronics|international|holdings)\b")]
    private static partial Regex CompanySuffix();
}
