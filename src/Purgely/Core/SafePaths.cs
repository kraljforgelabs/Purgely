using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Purgely.Core;

/// <summary>
/// The single place that decides whether a file or folder may ever be removed.
/// Used by the scanners (to decide what to offer) and again by the Cleaner as a final guard.
/// </summary>
public static class SafePaths
{
    private static readonly Lazy<List<string>> _blocked = new(BuildBlocked);
    private static readonly Lazy<List<string>> _userContent = new(BuildUserContent);

    /// <summary>Folders whose contents belong to the user (never removed, only shortcuts in them).</summary>
    public static IReadOnlyList<string> UserContentFolders => _userContent.Value;

    /// <summary>Full path, long names (no 8.3), no \\?\ prefix. Empty when it can't be resolved.</summary>
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return "";
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\")) p = p[4..];
        if (p.StartsWith(@"\\")) return ""; // network path
        try { p = Path.GetFullPath(p); } catch (Exception) { return ""; }
        if (p.Contains('~')) p = LongName(p);
        return p.TrimEnd('\\');
    }

    public static bool IsRemovableFolder(string path, out string reason)
    {
        reason = "";
        var p = Canonical(path);
        if (p.Length == 0) { reason = "path can't be resolved or is on the network"; return false; }
        if (p.Length <= 3 || !Path.IsPathRooted(p)) { reason = "a whole drive"; return false; }
        if (FileMover.IsReparsePoint(p)) { reason = "a link (junction) – its target belongs to something else"; return false; }
        if (PathUtil.SameOrUnder(p, PathUtil.WindowsDir)) { reason = "inside Windows"; return false; }
        if (PathUtil.SameOrUnder(p, PathUtil.Normalize(AppContext.BaseDirectory)) ||
            PathUtil.IsUnder(PathUtil.Normalize(AppContext.BaseDirectory), p)) { reason = "Purgely itself"; return false; }
        if (PathUtil.SameOrUnder(p, Path.GetDirectoryName(Cleaner.UndoRoot)!) || IsDriveUndo(p)) { reason = "Purgely's Undo storage"; return false; }
        foreach (var b in _blocked.Value)
            if (PathUtil.SameOrUnder(b, p)) { reason = $"a Windows or user folder ({Path.GetFileName(b)})"; return false; }
        foreach (var u in _userContent.Value)
            if (PathUtil.IsUnder(p, u)) { reason = $"inside your {Path.GetFileName(u)} folder"; return false; }
        if (Keywords.Protected.Contains(Keywords.Norm(Path.GetFileName(p)))) { reason = "a protected name"; return false; }
        return true;
    }

    public static bool IsRemovableFile(string path, out string reason)
    {
        reason = "";
        var p = Canonical(path);
        if (p.Length == 0) { reason = "path can't be resolved or is on the network"; return false; }
        // The only files inside Windows Purgely may remove: orphaned cached installers in C:\Windows\Installer.
        var winInstaller = Path.Combine(PathUtil.WindowsDir, "Installer");
        var cachedInstaller = string.Equals(Path.GetDirectoryName(p), winInstaller, StringComparison.OrdinalIgnoreCase) &&
                              (p.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".msp", StringComparison.OrdinalIgnoreCase));
        if (PathUtil.IsUnder(p, PathUtil.WindowsDir) && !cachedInstaller) { reason = "inside Windows"; return false; }
        if (PathUtil.SameOrUnder(p, Path.GetDirectoryName(Cleaner.UndoRoot)!) || IsDriveUndo(p)) { reason = "Purgely's Undo storage"; return false; }
        var isShortcut = p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        if (!isShortcut)
            foreach (var u in _userContent.Value)
                if (PathUtil.IsUnder(p, u)) { reason = $"inside your {Path.GetFileName(u)} folder"; return false; }
        return true;
    }

    private static bool IsDriveUndo(string p)
    {
        var root = Path.GetPathRoot(p) ?? "";
        return root.Length > 0 && PathUtil.SameOrUnder(p, Path.Combine(root, Cleaner.DriveUndoFolderName));
    }

    /// <summary>A folder directly on a drive (D:\Games) is never pre-selected, only offered.</summary>
    public static bool IsShallow(string path)
    {
        var p = Canonical(path);
        return p.Length == 0 || p.TrimEnd('\\').Count(c => c == '\\') < 2;
    }

    // ---------- lists ----------

    private static List<string> BuildBlocked()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<string>
        {
            profile, Path.GetDirectoryName(profile) ?? "", // C:\Users
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
            F(Environment.SpecialFolder.CommonProgramFiles), F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            local, Path.Combine(local, "Programs"), Path.Combine(local, "Temp"), Path.Combine(profile, "AppData"),
            Path.Combine(profile, "AppData", "LocalLow"),
            F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms),
            F(Environment.SpecialFolder.StartMenu), F(Environment.SpecialFolder.CommonStartMenu),
            F(Environment.SpecialFolder.Startup), F(Environment.SpecialFolder.CommonStartup),
            F(Environment.SpecialFolder.Fonts), F(Environment.SpecialFolder.System), PathUtil.WindowsDir,
            Environment.GetEnvironmentVariable("PUBLIC") ?? "",
        };
        list.AddRange(BuildUserContent());
        foreach (var drive in SafeDrives())
        {
            list.Add(Path.Combine(drive, "Program Files"));
            list.Add(Path.Combine(drive, "Program Files (x86)"));
            list.Add(Path.Combine(drive, "ProgramData"));
            list.Add(Path.Combine(drive, "Users"));
            list.Add(Path.Combine(drive, "SteamLibrary"));
            list.Add(Path.Combine(drive, "SteamLibrary", "steamapps"));
            list.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common"));
        }
        return Clean(list);
    }

    private static List<string> BuildUserContent()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            F(Environment.SpecialFolder.MyDocuments), F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyMusic), F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.CommonDocuments), F(Environment.SpecialFolder.CommonPictures),
            F(Environment.SpecialFolder.CommonMusic), F(Environment.SpecialFolder.CommonVideos),
            F(Environment.SpecialFolder.Favorites),
            KnownFolder("374DE290-123F-4565-9164-39C4925E467B"), // Downloads
            KnownFolder("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), // Saved Games
            Path.Combine(profile, "Downloads"), Path.Combine(profile, "Saved Games"),
            Path.Combine(profile, "Contacts"), Path.Combine(profile, "Links"), Path.Combine(profile, "Searches"),
            Path.Combine(profile, "source"), Path.Combine(profile, "OneDrive"),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        };
        try
        {
            list.AddRange(Directory.EnumerateDirectories(profile, "OneDrive*"));
            list.AddRange(Directory.EnumerateDirectories(profile, "Dropbox*"));
            list.AddRange(Directory.EnumerateDirectories(profile, "Google Drive*"));
        }
        catch (Exception) { }
        return Clean(list);
    }

    private static List<string> Clean(IEnumerable<string> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => PathUtil.Normalize(p))
        .Where(p => p.Length > 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IEnumerable<string> SafeDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string KnownFolder(string guid)
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out var ptr) != 0) return "";
            try { return Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch (Exception) { return ""; }
    }

    private static string LongName(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            return GetLongPathName(path, sb, sb.Capacity) > 0 ? sb.ToString() : path;
        }
        catch (Exception) { return path; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);
}
