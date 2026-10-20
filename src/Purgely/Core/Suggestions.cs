using System.IO;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>
/// Gentle hints in the program list – never actions: programs that show no sign of use for months (from Windows'
/// Prefetch, which records when a program last ran) and several programs doing the same job.
/// </summary>
public static class Suggestions
{
    private static readonly Regex NeverSuggest = new(
        @"redistributable|runtime|driver|\bsdk\b|framework|webview|vulkan|physx|chipset|bluetooth|realtek|intel|nvidia|amd |" +
        @"\bupdate\b|helper|service|security|defender|antivirus|backup|windows|microsoft (edge|office|365)|purgely",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Same brand, different job (Avast Secure Browser, Norton Password Manager, McAfee WebAdvisor…).</summary>
    private static readonly Regex NotTheSameJob = new(@"Browser|VPN|Password|WebAdvisor|Safe ?Web|Driver Updater|Cleanup|Utilities",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record Category(string Name, string Hint, Regex Pattern);

    private static Category C(string name, string hint, string pattern) =>
        new(name, hint, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static readonly Category[] Categories =
    {
        C("browsers", "you probably need one or two", @"^(Google Chrome|Mozilla Firefox|Opera( GX)?( Stable)?|Brave|Vivaldi|Yandex|Waterfox|LibreWolf)\b"),
        C("archivers", "one is enough", @"^(WinRAR|7-Zip|WinZip|Bandizip|PeaZip|NanaZip)\b"),
        C("PDF readers", "one is enough", @"^(Adobe Acrobat( Reader)?|Foxit (PDF )?Reader|Foxit PDF Editor|SumatraPDF|PDF-XChange|Nitro (PDF|Pro))\b"),
        C("antivirus programs", "two antivirus programs slow the PC and can block each other", @"^(Avast|AVG (Antivirus|Internet)|McAfee|Norton|Kaspersky|Bitdefender|ESET|Avira|Panda Dome|Total ?AV|360 Total)\b"),
        C("media players", "one is usually enough", @"^(VLC media player|PotPlayer|MPC-HC|KMPlayer|GOM Player|Media Player Classic)\b"),
        C("PC cleaners", "Purgely already does this job", @"^(CCleaner|Wise (Care|Disk Cleaner|Registry)|Advanced SystemCare|Glary Utilities|Revo Uninstaller|IObit Uninstaller|BleachBit)\b"),
    };

    /// <summary>Sets <see cref="InstalledProgram.Suggestion"/> on the programs. Returns how many got one.</summary>
    public static int Analyze(IReadOnlyList<InstalledProgram> programs)
    {
        var live = programs.Where(p => !p.IsOrphan && !p.IsRemovedEarlier && !p.IsStoreApp && !p.IsPortable && !p.IsLauncherGame).ToList();
        foreach (var p in programs) p.Suggestion = "";

        foreach (var c in Categories)
        {
            var found = live.Where(p => c.Pattern.IsMatch(p.Name.Trim()) && !NotTheSameJob.IsMatch(p.Name)).ToList();
            var names = found.Select(p => Short(p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count < 2) continue;
            foreach (var p in found)
                p.Suggestion = $"One of {names.Count} {c.Name} ({string.Join(", ", names)}) – {c.Hint}";
        }

        var lastRun = PrefetchTimes();
        if (lastRun.Count > 50) // Prefetch is readable and in use; otherwise "no sign of use" would mean nothing
        {
            var cutoff = DateTime.Now.AddMonths(-6);
            foreach (var p in live)
            {
                if (p.Suggestion.Length > 0 || p.IsRuntime || NeverSuggest.IsMatch(p.Name) || NeverSuggest.IsMatch(p.Publisher)) continue;
                if (p.InstallDate is not { } installed || installed > cutoff) continue;
                var exes = ProgramExes(p);
                if (exes.Count == 0) continue; // can't tell
                // Prefetch file names keep only the first 29 characters of the EXE name.
                var last = exes.Select(e => e.Length > 29 ? e[..29] : e)
                               .Select(e => lastRun.TryGetValue(e, out var seen) ? seen : (DateTime?)null).Max();
                if (last is { } t && t > cutoff) continue;
                p.Suggestion = last is { } seen
                    ? $"Last used {seen.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} – uninstall it if you don't need it"
                    : "No sign of use in the last months – uninstall it if you don't need it";
            }
        }
        return programs.Count(p => p.Suggestion.Length > 0);
    }

    private static string Short(string name) => Regex.Replace(name, @"\s+[\d(].*$", "").Trim();

    /// <summary>EXE name → last time Windows saw it run (Prefetch files: NAME.EXE-1A2B3C4D.pf).</summary>
    private static Dictionary<string, DateTime> PrefetchTimes()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(PathUtil.WindowsDir, "Prefetch"), "*.pf"))
            {
                var name = Path.GetFileName(file);
                var dash = name.LastIndexOf('-');
                if (dash <= 0) continue;
                var exe = name[..dash];
                var time = File.GetLastWriteTime(file);
                if (!map.TryGetValue(exe, out var old) || time > old) map[exe] = time;
            }
        }
        catch (Exception) { } // not administrator or Prefetch turned off
        return map;
    }

    /// <summary>EXE names in the program's folder (two levels deep, at most 200).</summary>
    private static List<string> ProgramExes(InstalledProgram p)
    {
        var folder = p.InstallLocation.Trim('"', ' ');
        if (folder.Length <= 3) folder = DependencyMap.ProgramFolder(p) ?? "";
        if (folder.Length <= 3 || !Directory.Exists(folder) || !SafePaths.IsRemovableFolder(folder, out _)) return new();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true,
                                                   AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(folder, "*.exe", options).Take(200)
                .Select(Path.GetFileName).Where(n => n is not null && !Regex.IsMatch(n, "unins|setup|update|crash|report|helper", RegexOptions.IgnoreCase))
                .Select(n => n!).ToList();
        }
        catch (Exception) { return new(); }
    }
}
