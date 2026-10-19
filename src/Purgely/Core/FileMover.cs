using System.IO;

namespace Purgely.Core;

/// <summary>
/// Moves files into an Undo package and back, coping with locked files.
/// Safety rules (council review):
/// - a source file is deleted only after its copy was verified (same length);
/// - files that could not be backed up are left where they are, never scheduled for deletion;
/// - junctions and symbolic links are never followed – their targets belong to someone else;
/// - free space is checked before a copy across drives.
/// </summary>
public static class FileMover
{
    public sealed record MoveResult(bool BackedUp, int ScheduledForReboot, int NotBackedUp, string Error);

    public static MoveResult MoveToBackup(string source, string destination, bool isFolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (IsReparsePoint(source))
            return new(false, 0, 0, "is a link (junction or symbolic link) – skipped, its target is not ours");

        // Fast path: same drive and no links inside → a simple rename.
        if (SameDrive(source, destination) && (!isFolder || !ContainsReparsePoint(source)))
        {
            try
            {
                if (isFolder) Directory.Move(source, destination);
                else File.Move(source, destination);
                return new(true, 0, 0, "");
            }
            catch (Exception) { /* something is locked – fall back to verified copy + delete */ }
        }

        if (!SameDrive(source, destination))
        {
            var needed = isFolder ? SizeOf(source) : SafeLength(source);
            var free = FreeSpace(destination);
            if (free >= 0 && needed + 200L * 1024 * 1024 > free)
                return new(false, 0, 0,
                    $"not enough free space for the Undo backup ({Sizes.Format(needed)} needed, {Sizes.Format(free)} free) – nothing was removed");
        }

        if (isFolder)
        {
            // Mirror the folder tree first, so empty folders are backed up (and restored) too.
            Directory.CreateDirectory(destination);
            foreach (var dir in WalkDirs(source))
                try { Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir))); }
                catch (Exception) { }
        }
        var files = isFolder ? WalkFiles(source) : new List<string> { source };
        int scheduled = 0, notBackedUp = 0, copied = 0;
        foreach (var file in files)
        {
            var target = isFolder ? Path.Combine(destination, Path.GetRelativePath(source, file)) : destination;
            if (!CopyVerified(file, target)) { notBackedUp++; continue; }
            copied++;
            if (!TryDelete(file) && Shell.DeleteOnReboot(file)) scheduled++;
        }

        if (isFolder && (copied > 0 || files.Count == 0)) RemoveEmptyDirs(source);
        if (copied == 0 && files.Count > 0)
            return new(false, 0, notBackedUp, "could not back up any file – nothing was removed");
        return new(true, scheduled, notBackedUp, "");
    }

    /// <summary>
    /// Puts a backup back (merging into an existing folder, never overwriting). The backup copy is deleted only for
    /// files that were restored. Returns how many files were kept in the package because a newer one exists.
    /// </summary>
    public static int MoveBack(string backup, string original, bool isFolder)
    {
        if (SameDrive(backup, original) && !(isFolder ? Directory.Exists(original) : File.Exists(original)))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (isFolder) Directory.Move(backup, original);
                else File.Move(backup, original);
                return 0;
            }
            catch (Exception) { /* fall back to copy */ }
        }
        var failed = 0;
        var skipped = 0;
        if (isFolder)
            foreach (var dir in WalkDirs(backup))
                try { Directory.CreateDirectory(Path.Combine(original, Path.GetRelativePath(backup, dir))); }
                catch (Exception) { }
        var files = isFolder ? WalkFiles(backup) : new List<string> { backup };
        foreach (var file in files)
        {
            var target = isFolder ? Path.Combine(original, Path.GetRelativePath(backup, file)) : original;
            if (File.Exists(target)) { skipped++; continue; } // never overwrite a newer file – the old copy stays in the package
            if (CopyVerified(file, target)) TryDelete(file);
            else failed++;
        }
        if (isFolder) RemoveEmptyDirs(backup);
        if (failed > 0) throw new IOException($"{failed} file(s) could not be restored – they are still in the Undo package");
        return skipped;
    }

    /// <summary>
    /// Deletes a folder or file that Purgely owns (an Undo package). Links are removed as links,
    /// never followed. Locked items are scheduled for deletion at restart. Returns that count.
    /// </summary>
    public static int DeleteBestEffort(string path, bool isFolder)
    {
        if (!isFolder)
            return TryDelete(path) ? 0 : (Shell.DeleteOnReboot(path) ? 1 : 0);
        if (!Directory.Exists(path)) return 0;
        if (IsReparsePoint(path)) { RemoveLink(path); return 0; }

        var scheduled = 0;
        foreach (var file in WalkFiles(path))
            if (!TryDelete(file) && Shell.DeleteOnReboot(file)) scheduled++;
        foreach (var link in WalkLinks(path)) RemoveLink(link);
        RemoveEmptyDirs(path, scheduleLocked: true);
        return scheduled;
    }

    // ---------- walking without following links ----------

    /// <summary>All regular files below <paramref name="root"/>; never enters junctions or symlinked folders.</summary>
    public static List<string> WalkFiles(string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
                if (!IsReparsePoint(f)) result.Add(f);
            foreach (var d in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
                if (!IsReparsePoint(d)) stack.Push(d);
        }
        return result;
    }

    private static List<string> WalkDirs(string root)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            result.Add(dir);
            foreach (var d in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
                if (!IsReparsePoint(d)) stack.Push(d);
        }
        return result;
    }

    public static List<string> WalkLinks(string root)
    {
        var result = new List<string>();
        foreach (var dir in WalkDirs(root))
        {
            foreach (var d in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
                if (IsReparsePoint(d)) result.Add(d);
            foreach (var f in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
                if (IsReparsePoint(f)) result.Add(f);
        }
        return result;
    }

    public static long SizeOf(string folder)
    {
        long total = 0;
        foreach (var f in WalkFiles(folder)) total += SafeLength(f);
        return total;
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    private static bool ContainsReparsePoint(string folder) => WalkLinks(folder).Count > 0;

    /// <summary>Removes only the link entry itself; the target is untouched.</summary>
    private static void RemoveLink(string link)
    {
        try
        {
            if (Directory.Exists(link)) Directory.Delete(link, false); // non-recursive: removes the link only
            else File.Delete(link);
        }
        catch (Exception) { /* leave it */ }
    }

    private static void RemoveEmptyDirs(string root, bool scheduleLocked = false)
    {
        foreach (var dir in WalkDirs(root).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
            }
            catch (Exception) { if (scheduleLocked) Shell.DeleteOnReboot(dir); }
        }
    }

    // ---------- helpers ----------

    private static bool CopyVerified(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            return new FileInfo(destination).Length == new FileInfo(source).Length;
        }
        catch (Exception) { return false; }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch (Exception) { return 0; }
    }

    private static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception) { return -1; }
    }

    private static List<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try { return enumerate().ToList(); }
        catch (Exception) { return new List<string>(); }
    }

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
