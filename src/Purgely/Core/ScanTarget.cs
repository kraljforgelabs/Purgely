using System.IO;

namespace Purgely.Core;

/// <summary>What we are looking for: names, publisher, install folder and date of one program.</summary>
public sealed class ScanTarget
{
    public required string DisplayName { get; init; }
    public string InstallLocation { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public string? UninstallKey { get; init; }
    public bool StillInstalled { get; init; }

    /// <summary>Normalized names that clearly identify the program.</summary>
    public HashSet<string> Strong { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Partial names (e.g. first word) – weaker evidence.</summary>
    public HashSet<string> Weak { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Names used inside the publisher's own folder.</summary>
    public HashSet<string> Nested { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Normalized publisher names (e.g. "iobit").</summary>
    public HashSet<string> Publishers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Size Windows recorded for the program (EstimatedSize), used as evidence for a found folder.</summary>
    public long? ExpectedSizeKb { get; init; }

    public static ScanTarget FromProgram(InstalledProgram p, bool stillInstalled)
    {
        var t = new ScanTarget
        {
            DisplayName = p.Name,
            // No InstallLocation (common for repacks): use the folder of its icon or uninstaller instead.
            InstallLocation = p.InstallLocation.Length > 3 ? p.InstallLocation : DependencyMap.ProgramFolder(p) ?? "",
            ExpectedSizeKb = p.SizeKb,
            InstallDate = p.InstallDate,
            UninstallKey = p.RegistryKey,
            StillInstalled = stillInstalled,
        };
        t.Fill(p.Name, p.Publisher, p.InstallLocation);
        return t;
    }

    public static ScanTarget FromName(string name)
    {
        var t = new ScanTarget { DisplayName = name };
        t.Fill(name, "", "");
        var n = Keywords.Norm(name);
        if (n.Length >= 3 && !Keywords.IsGeneric(n))
        {
            t.Strong.Add(n);
            t.Nested.Add(n);
            t.Weak.Remove(n);
        }
        return t;
    }

    public bool IsEmpty => Strong.Count == 0 && Weak.Count == 0;

    private void Fill(string name, string publisher, string location)
    {
        var clean = Keywords.CleanName(name);
        var full = Keywords.Norm(clean);
        if (full.Length >= 4 && !Keywords.IsGeneric(full)) Strong.Add(full);

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Keywords.StopWords.Contains(w)).ToList();
        if (words.Count >= 2) Strong.Add(words[0] + words[1]);
        if (words.Count > 0 && words[0].Length >= 4 && !Keywords.Protected.Contains(words[0])) Weak.Add(words[0]);

        if (location.Length > 0)
        {
            var folder = Keywords.Norm(Path.GetFileName(location.Trim('"', ' ').TrimEnd('\\', '/')));
            if (folder.Length >= 3 && !Keywords.IsGeneric(folder)) Strong.Add(folder);
        }

        var pub = Keywords.CleanPublisher(publisher);
        if (pub.Length >= 3 && !Keywords.StopWords.Contains(pub)) Publishers.Add(pub);

        Nested.UnionWith(Strong);
        Nested.UnionWith(words);
        Weak.ExceptWith(Strong);
    }
}
