using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Purgely.Core;

public enum TimelineEventType { Installed, Updated, Removed, InstalledTogether, Uninstalled, Cleaned, Restored, TrialStarted, TrialKept, TrialReverted, StartupChanged, SettingChanged }

public sealed class TimelineEvent
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public DateTime Time { get; set; }
    public TimelineEventType Type { get; set; }
    public string Program { get; set; } = "";
    public string Detail { get; set; } = "";

    [JsonIgnore] public string TimeText => Time.ToString("HH:mm");
    [JsonIgnore] public string DateText => Time.Date == DateTime.Today ? "Today"
        : Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : Time.ToString("dddd, d MMMM yyyy", English);
    [JsonIgnore] public bool IsWarning => Type == TimelineEventType.InstalledTogether;
    [JsonIgnore] public string TypeText => Type switch
    {
        TimelineEventType.Installed => "➕ Installed",
        TimelineEventType.Updated => "⟳ Updated",
        TimelineEventType.Removed => "➖ Removed",
        TimelineEventType.InstalledTogether => "⚠ Installed together",
        TimelineEventType.Uninstalled => "🗑 Uninstall started",
        TimelineEventType.Cleaned => "🧹 Leftovers cleaned",
        TimelineEventType.Restored => "↶ Restored",
        TimelineEventType.TrialStarted => "🧪 Trial install",
        TimelineEventType.TrialKept => "🧪 Trial kept",
        TimelineEventType.TrialReverted => "🧪 Trial reverted",
        TimelineEventType.StartupChanged => "⚡ Startup",
        TimelineEventType.SettingChanged => "⚙ Windows setting",
        _ => Type.ToString(),
    };
}

/// <summary>PC Timeline: history of every install, update, removal and Purgely action.</summary>
public static class Timeline
{
    private sealed class KnownProgram
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        // Kept so a program removed outside Purgely can still be scanned for leftovers later.
        public string Publisher { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string DisplayIcon { get; set; } = "";
        public DateTime? InstallDate { get; set; }
    }

    /// <summary>A program that disappeared from Windows' list (removed by Purgely or by anything else).</summary>
    public sealed class RemovedProgram
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string DisplayIcon { get; set; } = "";
        public DateTime? InstallDate { get; set; }
        public DateTime RemovedAt { get; set; }
        /// <summary>Leftovers were scanned (and maybe cleaned) since – no longer shown as "left traces?".</summary>
        public bool Handled { get; set; }
    }

    /// <summary>Bundleware groups found by a Sync that the home screen has not shown yet.</summary>
    public static readonly List<List<InstalledProgram>> PendingBundles = new();

    private const int CurrentFormat = 6;

    private sealed class TimelineData
    {
        public int Version { get; set; } = CurrentFormat;
        public bool Seeded { get; set; }
        public List<TimelineEvent> Events { get; set; } = new();
        public Dictionary<string, KnownProgram> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RemovedProgram> Removed { get; set; } = new();
        /// <summary>Names whose leftovers were already looked at (for removals recorded before details were kept).</summary>
        public HashSet<string> HandledNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Programs whose leftovers were scanned before Windows dropped them from its list (force uninstall).</summary>
        public HashSet<string> HandledKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Programs registered within this many minutes of each other count as "installed together".</summary>
    private static readonly TimeSpan BundleWindow = TimeSpan.FromMinutes(3);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Purgely");
    private static string FilePath => Path.Combine(DataFolder, "timeline.json");

    public static List<TimelineEvent> All()
    {
        lock (Gate) return Load().Events.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>
    /// Programs removed in the last <paramref name="days"/> days whose leftovers were not looked at yet –
    /// as program entries, so they can be scanned like any removed program.
    /// </summary>
    public static List<InstalledProgram> RecentlyRemoved(int days = 60)
    {
        lock (Gate)
        {
            var data = Load();
            var since = DateTime.Now.AddDays(-days);
            var list = data.Removed.Where(r => !r.Handled && r.RemovedAt >= since).ToList();

            // Removals recorded before Purgely kept program details: rebuild them from the Timeline (name only).
            var known = new HashSet<string>(data.Known.Values.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
            var listed = new HashSet<string>(data.Removed.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var e in data.Events.Where(e => e.Type == TimelineEventType.Removed && e.Time >= since)
                                         .OrderByDescending(e => e.Time))
            {
                if (known.Contains(e.Program) || listed.Contains(e.Program) || data.HandledNames.Contains(e.Program)) continue;
                var cleaned = data.Events.Any(c => c.Type == TimelineEventType.Cleaned && c.Time >= e.Time &&
                                                   c.Program.Contains(e.Program, StringComparison.OrdinalIgnoreCase));
                // Uninstalled through Purgely: its leftovers were scanned right after.
                var viaPurgely = data.Events.Any(u => u.Type == TimelineEventType.Uninstalled && u.Program == e.Program &&
                                                      u.Time <= e.Time && u.Time >= e.Time.AddDays(-1));
                if (cleaned || viaPurgely) continue;
                listed.Add(e.Program);
                list.Add(new RemovedProgram { Key = $"timeline:{e.Program}", Name = e.Program, RemovedAt = e.Time,
                                              Version = e.Detail.Replace("Version ", "") });
            }

            // An upgrade that changed the registry key (new MSI product code) is not a removal: same product name
            // without version, or same install folder, is still installed.
            var currentNames = new HashSet<string>(data.Known.Values.Select(k => Keywords.CleanName(k.Name)), StringComparer.OrdinalIgnoreCase);
            var currentFolders = new HashSet<string>(data.Known.Values.Where(k => k.InstallLocation.Length > 3)
                .Select(k => PathUtil.Normalize(k.InstallLocation)), StringComparer.OrdinalIgnoreCase);
            list.RemoveAll(r => currentNames.Contains(Keywords.CleanName(r.Name)) ||
                                (r.InstallLocation.Length > 3 && currentFolders.Contains(PathUtil.Normalize(r.InstallLocation))));

            return list
                .OrderByDescending(r => r.RemovedAt)
                .Select(r => new InstalledProgram
                {
                    Name = r.Name, Publisher = r.Publisher, Version = r.Version, InstallLocation = r.InstallLocation,
                    DisplayIcon = r.DisplayIcon, InstallDate = r.InstallDate, RegistryKey = r.Key,
                    OrphanReason = $"Removed {r.RemovedAt:yyyy-MM-dd} – leftovers not checked yet",
                    IsRemovedEarlier = true,
                })
                .ToList();
        }
    }

    /// <summary>Leftovers of a removed program were scanned – stop listing it.</summary>
    public static void MarkRemovedHandled(string key)
    {
        lock (Gate)
        {
            var data = Load();
            // Not in the removed list yet (the program is still listed until the cleanup removes its entry):
            // remember the key so the coming removal is recorded as already handled.
            if (!data.Removed.Any(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) && !key.StartsWith("timeline:"))
                data.HandledKeys.Add(key);
            foreach (var r in data.Removed.Where(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                r.Handled = true;
                data.HandledNames.Add(r.Name);
            }
            if (key.StartsWith("timeline:")) data.HandledNames.Add(key["timeline:".Length..]);
            Save(data);
        }
    }

    /// <summary>
    /// Clears the history list (events older than <paramref name="olderThanDays"/> days; 0 = all). What Purgely needs to
    /// notice removals and to find leftovers of recently removed programs is kept.
    /// </summary>
    public static int ClearHistory(int olderThanDays = 0)
    {
        lock (Gate)
        {
            var data = Load();
            var cutoff = DateTime.Now.AddDays(-olderThanDays);
            var removed = data.Events.RemoveAll(e => olderThanDays == 0 || e.Time < cutoff);
            Save(data);
            return removed;
        }
    }

    public static void Log(TimelineEventType type, string program, string detail = "")
    {
        lock (Gate)
        {
            var data = Load();
            data.Events.Add(new TimelineEvent { Time = DateTime.Now, Type = type, Program = program, Detail = detail });
            Save(data);
        }
    }

    /// <summary>
    /// Compares the current program list with the last one Purgely saw and records the differences.
    /// Returns groups of NEW programs that were installed together by different publishers (possible bundleware).
    /// </summary>
    public static List<List<InstalledProgram>> Sync(IReadOnlyList<InstalledProgram> programs)
    {
        lock (Gate)
        {
            var data = Load();
            var current = programs.Where(p => !p.IsOrphan)
                .GroupBy(p => p.RegistryKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var newGroups = new List<List<InstalledProgram>>();

            if (!data.Seeded)
            {
                // First run: rebuild the history from what Windows remembers.
                var rewritten = MassRewrites(current.Values);
                foreach (var p in current.Values)
                    data.Events.Add(new TimelineEvent
                    {
                        Time = rewritten.Contains(p) ? p.InstallDate ?? p.RegisteredAt ?? DateTime.Now
                                                     : p.RegisteredAt ?? p.InstallDate ?? DateTime.Now,
                        Type = TimelineEventType.Installed,
                        Program = p.Name,
                        Detail = Describe(p),
                    });
                foreach (var group in FindGroups(current.Values))
                    data.Events.Add(GroupEvent(group, "A program from another publisher was newly installed at the same moment – possible bundle"));
                data.Seeded = true;
            }
            else
            {
                var added = new List<InstalledProgram>();
                foreach (var (key, p) in current)
                {
                    if (!data.Known.TryGetValue(key, out var known))
                    {
                        added.Add(p);
                        data.Events.Add(new TimelineEvent
                        {
                            Time = p.RegisteredAt ?? DateTime.Now, Type = TimelineEventType.Installed,
                            Program = p.Name, Detail = Describe(p),
                        });
                    }
                    else if (!string.Equals(known.Version, p.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        data.Events.Add(new TimelineEvent
                        {
                            Time = p.RegisteredAt ?? DateTime.Now, Type = TimelineEventType.Updated,
                            Program = p.Name, Detail = $"{known.Version} → {p.Version}",
                        });
                    }
                }
                foreach (var (key, known) in data.Known)
                    if (!current.ContainsKey(key))
                    {
                        data.Events.Add(new TimelineEvent
                        {
                            Time = DateTime.Now, Type = TimelineEventType.Removed,
                            Program = known.Name, Detail = known.Version.Length > 0 ? $"Version {known.Version}" : "",
                        });
                        data.Removed.RemoveAll(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
                        data.Removed.Add(new RemovedProgram
                        {
                            Key = key, Name = known.Name, Publisher = known.Publisher, Version = known.Version,
                            InstallLocation = known.InstallLocation, DisplayIcon = known.DisplayIcon,
                            InstallDate = known.InstallDate, RemovedAt = DateTime.Now,
                            Handled = data.HandledKeys.Remove(key), // already scanned (e.g. force uninstall)
                        });
                    }
                // A program that came back (reinstalled) is no longer "removed".
                data.Removed.RemoveAll(r => current.ContainsKey(r.Key));
                if (data.Removed.Count > 200) data.Removed = data.Removed.OrderByDescending(r => r.RemovedAt).Take(200).ToList();

                var addedSet = added.ToHashSet();
                newGroups = FindGroups(current.Values).Where(g => g.Any(addedSet.Contains)).ToList();
                foreach (var group in newGroups)
                    data.Events.Add(GroupEvent(group, "Installed at the same time by different publishers – one installer may have added extra software"));
            }

            data.Known = current.ToDictionary(kv => kv.Key,
                kv => new KnownProgram
                {
                    Name = kv.Value.Name, Version = kv.Value.Version, Publisher = kv.Value.Publisher,
                    InstallLocation = kv.Value.InstallLocation, DisplayIcon = kv.Value.DisplayIcon,
                    InstallDate = kv.Value.InstallDate,
                }, StringComparer.OrdinalIgnoreCase);
            Save(data);
            lock (PendingBundles) PendingBundles.AddRange(newGroups);
            return newGroups;
        }
    }

    /// <summary>
    /// Bundleware Detector: a program that was NEWLY installed within minutes of another program
    /// (installed or updated) from a different publisher.
    /// </summary>
    public static List<List<InstalledProgram>> FindGroups(IEnumerable<InstalledProgram> programs)
    {
        var all = programs.ToList();
        var rewritten = MassRewrites(all); // decided on ALL programs, before any filtering
        var candidates = all
            .Where(p => p.RegisteredAt is not null && !rewritten.Contains(p) && !DependencyMap.IsRuntimeName(p.Name) &&
                        !p.Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Clusters(candidates)
            .Where(g => g.Count is >= 2 and <= MaxBundleSize)
            .Where(g => g.Any(p => IsSlippedIn(p, g)))
            .ToList();
    }

    /// <summary>
    /// The typical bundle: a NEW program that is the only one from its publisher in the group,
    /// appearing while another publisher's program was being installed or updated.
    /// </summary>
    private static bool IsSlippedIn(InstalledProgram p, List<InstalledProgram> group)
    {
        if (!IsFreshInstall(p)) return false;
        var others = group.Where(o => !ReferenceEquals(o, p)).ToList();
        return others.Count > 0 && others.All(o => !SamePublisher(o, p));
    }

    private static bool SamePublisher(InstalledProgram a, InstalledProgram b)
    {
        var pa = Keywords.CleanPublisher(a.Publisher);
        var pb = Keywords.CleanPublisher(b.Publisher);
        return pa.Length > 0 && (pa == pb || pa.Contains(pb) || pb.Contains(pa)) && pb.Length > 0;
    }

    /// <summary>Larger groups are Windows rewriting the registry (feature update, migration), not a bundle.</summary>
    private const int MaxBundleSize = 6;

    /// <summary>
    /// An entry whose original install date is much older than its last registry write was UPDATED, not installed.
    /// </summary>
    private static bool IsFreshInstall(InstalledProgram p)
    {
        if (p.RegisteredAt is not { } registered) return true;
        if (p.InstallDate is { } installed && Math.Abs((registered.Date - installed.Date).TotalDays) > 1)
            return false;

        // The program's folder is created at the first install and survives updates –
        // a folder much older than the registry entry means this was an update.
        try
        {
            if (DependencyMap.ProgramFolder(p) is { } folder &&
                Directory.GetCreationTime(folder) < registered.AddDays(-1))
                return false;
        }
        catch (Exception) { }
        return true;
    }

    /// <summary>Programs whose registry entry was rewritten together with many others at once.</summary>
    private static HashSet<InstalledProgram> MassRewrites(IEnumerable<InstalledProgram> programs) =>
        Clusters(programs.Where(p => p.RegisteredAt is not null).ToList())
            .Where(g => g.Count > MaxBundleSize)
            .SelectMany(g => g)
            .ToHashSet();

    private static List<List<InstalledProgram>> Clusters(List<InstalledProgram> programs)
    {
        var groups = new List<List<InstalledProgram>>();
        var cluster = new List<InstalledProgram>();
        foreach (var p in programs.OrderBy(p => p.RegisteredAt))
        {
            if (cluster.Count > 0 && p.RegisteredAt!.Value - cluster[^1].RegisteredAt!.Value > BundleWindow)
            {
                groups.Add(cluster);
                cluster = new List<InstalledProgram>();
            }
            cluster.Add(p);
        }
        if (cluster.Count > 0) groups.Add(cluster);
        return groups;
    }

    private static TimelineEvent GroupEvent(List<InstalledProgram> group, string why) => new()
    {
        Time = group[0].RegisteredAt ?? DateTime.Now,
        Type = TimelineEventType.InstalledTogether,
        Program = string.Join(" + ", group.Select(p => p.Name +
            (IsSlippedIn(p, group) ? " (new – slipped in?)" : IsFreshInstall(p) ? " (new)" : " (updated)"))),
        Detail = why,
    };

    private static string Describe(InstalledProgram p) =>
        string.Join("  •  ", new[] { p.Version.Length > 0 ? $"Version {p.Version}" : "", p.Publisher }
            .Where(s => s.Length > 0));

    private static TimelineData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<TimelineData>(File.ReadAllText(FilePath)) ?? new TimelineData();
                if (data.Version < CurrentFormat)
                {
                    // Older history was built with less accurate rules – rebuild it, keep Purgely's own actions.
                    data.Events.RemoveAll(e => e.Type is TimelineEventType.Installed or TimelineEventType.Updated
                        or TimelineEventType.Removed or TimelineEventType.InstalledTogether);
                    data.Known.Clear();
                    data.Seeded = false;
                    data.Version = CurrentFormat;
                }
                return data;
            }
        }
        catch (Exception) { /* damaged file – start a new history */ }
        return new TimelineData();
    }

    private static void Save(TimelineData data)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception) { /* the timeline must never break the app */ }
    }
}
