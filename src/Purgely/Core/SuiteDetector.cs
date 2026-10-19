using System.ComponentModel;
using System.IO;

namespace Purgely.Core;

/// <summary>A program that probably belongs to the same suite/bundle as the one being removed.</summary>
public sealed class RelatedProgram : INotifyPropertyChanged
{
    public required InstalledProgram Program { get; init; }
    public int Score { get; set; }
    public List<string> Reasons { get; } = new();
    public string Name => Program.Name;
    public string Publisher => Program.Publisher;
    public string Why => string.Join("  •  ", Reasons);
    public string ScoreText => Score >= 80 ? "Very likely" : Score >= 60 ? "Likely" : "Possibly";

    private bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Finds the rest of a suite (e.g. SOLIDWORKS Visualize after SOLIDWORKS is removed).</summary>
public static class SuiteDetector
{
    /// <param name="mainName">Name of the removed program (or the name the user typed).</param>
    public static List<RelatedProgram> FindRelated(string mainName, string mainPublisher, DateTime? mainInstalled,
                                                    string mainLocation, IReadOnlyList<InstalledProgram> programs,
                                                    string? excludeKey = null)
    {
        var words = Keywords.CleanName(mainName).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !Keywords.StopWords.Contains(w) && !Keywords.Protected.Contains(w))
            .ToList();
        var publisher = Keywords.CleanPublisher(mainPublisher);
        // The whole product phrase, without the company name: "Autodesk AutoCAD 2023" → "autocad",
        // "Visual Studio Code" → "visual studio code" (so Visual Studio 2022 is not pulled in).
        var productWords = words.Where(w => publisher.Length == 0 || Keywords.Norm(w) != publisher).ToList();
        if (productWords.Count == 0) productWords = words;
        var keyword = ProductPhrase(Keywords.CleanName(mainName).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                                    productWords.FirstOrDefault() ?? "");
        var parent = mainLocation.Length > 3 ? Path.GetDirectoryName(PathUtil.Normalize(mainLocation)) ?? "" : "";
        if (IsLibraryRoot(parent)) parent = "";

        var result = new List<RelatedProgram>();
        foreach (var p in programs)
        {
            if (p.IsOrphan || string.Equals(p.RegistryKey, excludeKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (DependencyMap.IsRuntimeName(p.Name)) continue; // runtimes are shared – the Dependency Map decides those

            var r = new RelatedProgram { Program = p };
            var name = Keywords.CleanName(p.Name);
            var samePublisher = publisher.Length >= 3 && Keywords.CleanPublisher(p.Publisher) == publisher;

            var nameMatch = keyword.Length > 0 && name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            var addOn = nameMatch && (name.Contains($"for {keyword}", StringComparison.OrdinalIgnoreCase) ||
                        new[] { "addin", "add in", "plugin", "plug in", "extension", "connector" }
                            .Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase)));
            if (nameMatch)
            {
                r.Score += addOn ? 70 : 50;
                r.Reasons.Add(addOn ? $"Add-on for {mainName}" : $"Its name mentions {keyword.ToUpperInvariant()}");
            }
            if (samePublisher)
            {
                r.Score += 25;
                r.Reasons.Add($"Same publisher ({p.Publisher})");
            }
            if (mainInstalled is { } installed && p.InstallDate is { } other && Math.Abs((installed.Date - other.Date).TotalDays) <= 1)
            {
                r.Score += 15;
                r.Reasons.Add("Installed at the same time");
            }
            if (parent.Length > 3 && p.InstallLocation.Length > 3 &&
                PathUtil.IsUnder(PathUtil.Normalize(p.InstallLocation), parent) && parent.Split('\\').Length > 2)
            {
                r.Score += 15;
                r.Reasons.Add("Installed in the same folder");
            }

            // A name match or same publisher is required – timing/folder alone is not enough.
            if (r.Score < 40 || r.Reasons.Count == 0) continue;
            if (!nameMatch && !samePublisher) continue;
            r.Score = Math.Min(r.Score, 99);
            // Ticked only when it is clearly part of the same product: an add-on, or the product name
            // plus the same publisher plus the same install day. Same brand alone is never enough.
            r.IsChecked = addOn || (nameMatch && samePublisher && r.Score >= 90 && keyword.Length >= 6);
            result.Add(r);
        }
        return result.OrderByDescending(r => r.Score).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Everyday words that are not a product name on their own ("Visual", "Studio", "Care"…).</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "visual", "studio", "smart", "quick", "easy", "advanced", "total", "super", "power", "data", "file", "files",
        "media", "game", "games", "cloud", "online", "desktop", "care", "center", "centre", "office", "sound", "audio",
        "video", "photo", "update", "updater", "driver", "drivers", "system", "security", "mobile", "connect", "control",
        "manager", "tools", "tool", "utility", "suite", "client", "service", "assistant", "helper", "launcher", "player",
        "home", "live", "creative", "business", "personal", "premium", "professional", "standard", "basic", "plus",
    };

    /// <summary>
    /// The first real product word, taken from the full name; while it is a common word the words right after it
    /// are added too ("visual studio code", "power bi desktop").
    /// </summary>
    internal static string ProductPhrase(List<string> allWords, string first)
    {
        if (first.Length == 0) return "";
        var start = allWords.FindIndex(w => string.Equals(w, first, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return first;
        var phrase = new List<string> { allWords[start] };
        for (var i = start + 1; i < allWords.Count && CommonWords.Contains(phrase[^1]); i++)
            phrase.Add(allWords[i]);
        return string.Join(' ', phrase);
    }

    /// <summary>Folders that hold many unrelated programs (game libraries, Program Files…).</summary>
    private static bool IsLibraryRoot(string folder)
    {
        if (folder.Length <= 3) return true;
        var name = Path.GetFileName(folder.TrimEnd('\\')).ToLowerInvariant();
        return name is "common" or "steamapps" or "steamlibrary" or "games" or "igre" or "epic games" or "gog games"
                   or "gog galaxy" or "program files" or "program files (x86)" or "programs" or "apps" or "windowsapps"
                   or "ubisoft game launcher" or "ea games" or "origin games" or "xboxgames"
               || folder.Split('\\').Length <= 2;
    }
}
