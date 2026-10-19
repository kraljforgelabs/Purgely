using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Purgely.Core.Trial;

/// <summary>Records every file and folder in the places installers write to, so "before" and "after" can be compared.</summary>
public static class FileSnapshot
{
    /// <summary>Path → (size, last write ticks). Folders have size -1.</summary>
    public sealed class Snapshot : Dictionary<string, (long Size, long Ticks)>
    {
        public Snapshot() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    private sealed record Plan(List<string> Deep, List<string> Shallow, List<string> LinksOnly,
                               List<string> Excluded, List<string> Forced);

    private static Plan MakePlan()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var roaming = F(Environment.SpecialFolder.ApplicationData);
        var programData = F(Environment.SpecialFolder.CommonApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var win = PathUtil.WindowsDir;
        var startMenus = new List<string>
        {
            Path.Combine(programData, @"Microsoft\Windows\Start Menu"),
            Path.Combine(roaming, @"Microsoft\Windows\Start Menu"),
        };

        var deep = new List<string>
        {
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            programData, roaming, local, Path.Combine(profile, "AppData", "LocalLow"),
            Path.Combine(win, "System32", "drivers"), Path.Combine(win, "System32", "Tasks"),
        };
        deep.AddRange(startMenus);

        var excluded = new List<string>
        {
            Path.Combine(local, "Temp"), Path.Combine(local, "Packages"), Path.Combine(local, "Microsoft"),
            Path.Combine(local, @"Google\Chrome\User Data"), Path.Combine(local, "BraveSoftware"),
            Path.Combine(local, "Mozilla"), Path.Combine(local, "D3DSCache"), Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "NVIDIA"), Path.Combine(local, "NVIDIA Corporation"),
            Path.Combine(roaming, "Microsoft"), Path.Combine(roaming, @"Mozilla\Firefox\Profiles"),
            Path.Combine(programData, "Microsoft"), Path.Combine(programData, "Purgely"),
            Path.Combine(programData, "NVIDIA Corporation"),
            Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
        };

        return new Plan(
            deep.Where(Directory.Exists).Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            new List<string> { Path.Combine(win, "System32"), Path.Combine(win, "SysWOW64") }.Where(Directory.Exists).ToList(),
            new List<string> { F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory) }
                .Where(d => d.Length > 0 && Directory.Exists(d)).ToList(),
            excluded.Select(PathUtil.Normalize).ToList(),
            startMenus.Select(PathUtil.Normalize).ToList());
    }

    /// <param name="expected">Size of an earlier snapshot, so progress can be shown as a percentage.</param>
    public static Snapshot Take(IProgress<string>? progress = null, int expected = 0)
    {
        var plan = MakePlan();
        var snap = new Snapshot();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
        };

        bool Excluded(string p) =>
            plan.Excluded.Any(e => PathUtil.SameOrUnder(p, e)) && !plan.Forced.Any(f => PathUtil.SameOrUnder(p, f));

        foreach (var root in plan.Deep)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                snap[dir] = (-1, 0);
                if (snap.Count % 5_000 < 1)
                    progress?.Report(expected > 0
                        ? $"Recording files… {Math.Min(99, snap.Count * 100L / expected)}%  ({snap.Count:N0} of ~{expected:N0})"
                        : $"Recording files… {snap.Count:N0} found");
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
                catch (Exception) { continue; }
                foreach (var entry in entries)
                {
                    try
                    {
                        if (entry is DirectoryInfo)
                        {
                            if (!Excluded(entry.FullName)) stack.Push(entry.FullName);
                        }
                        else if (entry is FileInfo file)
                        {
                            snap[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks);
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        foreach (var root in plan.Shallow)
            foreach (var file in SafeFiles(root, "*"))
                snap[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks);

        foreach (var root in plan.LinksOnly)
            foreach (var file in SafeFiles(root, "*").Where(f => f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                                                                 f.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase)))
                snap[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks);

        return snap;
    }

    private static List<FileInfo> SafeFiles(string dir, string pattern)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch (Exception) { return new List<FileInfo>(); }
    }

    public static void Save(string file, Snapshot snap)
    {
        using var fs = File.Create(file);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gz);
        foreach (var (path, (size, ticks)) in snap)
            writer.Write($"{size}\t{ticks}\t{path}\n");
    }

    public static Snapshot Load(string file)
    {
        var snap = new Snapshot();
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t', 3);
            if (parts.Length == 3)
                snap[parts[2]] = (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        return snap;
    }

    /// <summary>Fills the file part of the diff: new folders (top-most only), new files, changed files.</summary>
    public static void Compare(Snapshot before, Snapshot after, TrialDiff diff)
    {
        var addedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, (size, _)) in after)
            if (size < 0 && !before.ContainsKey(path)) addedDirs.Add(path);

        foreach (var dir in addedDirs)
            if (!addedDirs.Contains(Path.GetDirectoryName(dir) ?? "")) diff.AddedDirectories.Add(dir);

        foreach (var (path, (size, ticks)) in after)
        {
            if (size < 0) continue;
            if (!before.TryGetValue(path, out var old))
            {
                if (!addedDirs.Contains(Path.GetDirectoryName(path) ?? "")) diff.AddedFiles.Add(path);
            }
            else if (old.Size != size || old.Ticks != ticks)
            {
                diff.ModifiedFiles++;
                if (diff.ModifiedFileSamples.Count < 25) diff.ModifiedFileSamples.Add(path);
            }
        }
        diff.AddedDirectories.Sort(StringComparer.OrdinalIgnoreCase);
        diff.AddedFiles.Sort(StringComparer.OrdinalIgnoreCase);
    }
}
