using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Purgely.Core;

public enum FoundKind
{
    /// <summary>The icon, file or window belongs to this program.</summary>
    Program,
    /// <summary>A shortcut that starts something inside another program (a game in an emulator or launcher).</summary>
    ThroughLauncher,
    /// <summary>A folder with a program that was never installed.</summary>
    Portable,
    /// <summary>Part of Windows itself.</summary>
    Windows,
    /// <summary>A window of a Microsoft Store app – Windows hosts it, so the app can't be told from the window.</summary>
    StoreAppWindow,
    Purgely,
    /// <summary>A file that isn't part of any program (e.g. a setup file in Downloads).</summary>
    SingleFile,
    /// <summary>Several programs share the folder – the user picks in the list (search box filled in).</summary>
    Ambiguous,
    NotFound,
}

/// <param name="Name">What the user pointed at, in words (the shortcut's name or the file name).</param>
public sealed record FoundTarget(FoundKind Kind, InstalledProgram? Program, string Name, string Path = "");

/// <summary>
/// "Which program is this?" for a dropped icon or file, a right-clicked item or a picked window. Read-only: it only
/// finds the program – uninstalling goes through the normal steps, with the usual questions. When in doubt it answers
/// NotFound or Ambiguous (the user picks in the list) rather than guess.
/// </summary>
public static partial class ProgramFinder
{
    /// <summary>Sub-folders that are part of a program, not the program's own folder (D:\Tool\bin\tool.exe → D:\Tool).</summary>
    private static readonly HashSet<string> InnerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "x64", "x86", "win32", "win64", "amd64", "arm64", "binaries", "program", "app",
    };

    /// <summary>Programs that run other programs or scripts – their window says little about what is running in it.</summary>
    public static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "node.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "conhost.exe", "OpenConsole.exe", "WindowsTerminal.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "dllhost.exe", "msiexec.exe", "AutoHotkey.exe", "AutoHotkey64.exe",
    };

    public static FoundTarget Find(string path, IReadOnlyList<InstalledProgram> programs)
    {
        path = path.Trim().Trim('"');
        var live = programs.Where(p => !p.IsOrphan && !p.IsRemovedEarlier).ToList();
        var name = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
        var ext = System.IO.Path.GetExtension(path);

        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return FromUrl(ShellLink.ReadUrl(path), name, live);

        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var (target, args) = ShellLink.ReadLnk(path);
            FileDrop.Trace($"shortcut → '{target}' args '{args}'");
            // Windows Installer shortcuts point to C:\Windows\Installer\{ProductCode}\icon.exe.
            if (MsiProduct().Match(target) is { Success: true } msi &&
                live.FirstOrDefault(p => p.RegistryKey.EndsWith(msi.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { } product)
                return new FoundTarget(FoundKind.Program, product, name);
            // Shortcuts that don't point to a program file (Store apps, cmd /c, explorer shell:…): go by the name.
            if (target.Length == 0 || Hosts.Contains(System.IO.Path.GetFileName(target)) ||
                System.IO.Path.GetFileName(target).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return ByName(name, live) ?? new FoundTarget(FoundKind.NotFound, null, name);
            if (SteamApp(target, args) is { } steamId && BySteamId(steamId, live) is { } game)
                return new FoundTarget(FoundKind.Program, game, name);
            var found = FromPath(target, name, live);
            if (found.Kind == FoundKind.Program && args.Length > 0 && !Similar(name, found.Program!, target))
                return found with { Kind = FoundKind.ThroughLauncher };
            if (found.Kind is FoundKind.NotFound or FoundKind.SingleFile or FoundKind.Windows or FoundKind.Ambiguous &&
                ByName(name, live) is { } byName)
                return byName;
            return found;
        }

        return FromPath(path, name, live);
    }

    /// <summary>A file, folder or running program's .exe.</summary>
    public static FoundTarget FromPath(string path, string name, IReadOnlyList<InstalledProgram> live)
    {
        var full = SafePaths.Canonical(FinalPath(path));
        FileDrop.Trace($"path → '{full}'");
        if (full.Length == 0 || !File.Exists(full) && !Directory.Exists(full)) return new FoundTarget(FoundKind.NotFound, null, name);
        if (System.IO.Path.GetFileName(full).Equals("Purgely.exe", StringComparison.OrdinalIgnoreCase))
            return new FoundTarget(FoundKind.Purgely, null, name, full);
        if (System.IO.Path.GetFileName(full).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return new FoundTarget(FoundKind.StoreAppWindow, null, name, full);

        // The program whose folder holds it: the deepest folder wins (a launcher's game before the launcher), and a
        // program's own install folder counts more than a folder guessed from its icon or uninstaller.
        var matches = new List<(InstalledProgram P, int Length, int Weight)>();
        foreach (var p in live)
            foreach (var (folder, weight) in FoldersOf(p))
                if (PathUtil.SameOrUnder(full, folder)) matches.Add((p, folder.Length, weight));
        if (matches.Count > 0)
        {
            var top = matches.Max(m => (m.Length, m.Weight));
            var best = matches.Where(m => (m.Length, m.Weight) == top).Select(m => m.P)
                              .DistinctBy(p => p.RegistryKey, StringComparer.OrdinalIgnoreCase).ToList();
            if (best.Count > 1)
            {
                var fitting = best.Where(p => Similar(name, p, full)).ToList();
                if (fitting.Count == 1) best = fitting;
            }
            return best.Count == 1
                ? new FoundTarget(FoundKind.Program, best[0], name, full)
                : new FoundTarget(FoundKind.Ambiguous, null, name, full);
        }
        if (PathUtil.SameOrUnder(full, PathUtil.WindowsDir)) return new FoundTarget(FoundKind.Windows, null, name, full);

        // Not installed: maybe a portable program. Never inside Program Files, ProgramData and the like – a program
        // there was installed, even if Windows' list doesn't say where (the user then picks it in the list).
        var isFolder = Directory.Exists(full);
        if (InInstallArea(full) || LooksInstalled(full, name, live)) return new FoundTarget(FoundKind.NotFound, null, name, full);
        var dir = isFolder ? full : System.IO.Path.GetDirectoryName(full) ?? "";
        if (dir.Length <= 3 || !Usable(dir))
            return new FoundTarget(isFolder ? FoundKind.NotFound : FoundKind.SingleFile, null, name, full);
        while (InnerFolders.Contains(System.IO.Path.GetFileName(dir)) && System.IO.Path.GetDirectoryName(dir) is { Length: > 3 } up && Usable(up))
            dir = up;
        // The folder must hold one program: no installed program inside it, and one main .exe (or one named like the folder).
        if (live.Any(p => FoldersOf(p).Any(f => PathUtil.SameOrUnder(f.Folder, dir))))
            return new FoundTarget(FoundKind.NotFound, null, name, full);
        var exes = MainExes(dir);
        var folderName = Keywords.Norm(System.IO.Path.GetFileName(dir));
        string? exe = !isFolder && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
        if (exes.Count == 1) exe ??= exes[0];
        else
        {
            var named = exes.Where(e => Related(Keywords.Norm(System.IO.Path.GetFileNameWithoutExtension(e)), folderName)).ToList();
            if (exe is not null)
            {
                // One tool among several unrelated ones – the folder isn't this one program's.
                var fits = exes.Count == 0 || Related(Keywords.Norm(System.IO.Path.GetFileNameWithoutExtension(exe)), folderName) ||
                           named.Count == 1 && named[0].Equals(exe, StringComparison.OrdinalIgnoreCase);
                if (!fits) exe = null;
            }
            else if (named.Count == 1) exe = named[0];
        }
        if (exe is null) return new FoundTarget(isFolder ? FoundKind.NotFound : FoundKind.SingleFile, null, name, full);
        return new FoundTarget(FoundKind.Portable, PortableApps.ForFolder(dir, exe), name, full);
    }

    private static bool Related(string a, string b) =>
        a.Length >= 3 && b.Length >= 3 && (a.Contains(b) || b.Contains(a));

    /// <summary>Where installers put programs – a folder there is never offered as "portable".</summary>
    private static bool InInstallArea(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var area in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     local.Length > 0 ? System.IO.Path.Combine(local, "Programs") : "",
                 })
            if (area.Length > 3 && PathUtil.IsUnder(path, area.TrimEnd('\\'))) return true;
        // "D:\Program Files", "E:\Program Files (x86)" on other drives, and Store app folders anywhere
        var parts = path.Split('\\');
        return parts.Length > 2 && parts[1].StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file whose folder or product name matches an installed program: installed, just not where Windows says.</summary>
    private static bool LooksInstalled(string full, string name, IReadOnlyList<InstalledProgram> live)
    {
        var names = new List<string> { Keywords.Norm(Keywords.CleanName(name)) };
        var dir = Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full) ?? "";
        names.Add(Keywords.Norm(System.IO.Path.GetFileName(dir)));
        if (File.Exists(full))
        {
            try { names.Add(Keywords.Norm(System.Diagnostics.FileVersionInfo.GetVersionInfo(full).ProductName)); }
            catch (Exception) { }
        }
        var wanted = names.Where(n => n.Length >= 4 && !Keywords.StopWords.Contains(n)).ToHashSet();
        return wanted.Count > 0 && live.Any(p => !p.IsPortable && wanted.Contains(Keywords.Norm(Keywords.CleanName(p.Name))));
    }

    /// <summary>A folder Purgely may treat as one program's own (not a library of many, not a Windows or user folder).</summary>
    private static bool Usable(string folder) =>
        !SuiteDetector.IsLibraryRoot(folder) && SafePaths.IsRemovableFolder(folder, out _);

    private static List<string> MainExes(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(e => !Regex.IsMatch(System.IO.Path.GetFileName(e), "unins|setup|install|update|crash|helper|vc_?redist|dxsetup|report",
                    RegexOptions.IgnoreCase))
                .ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    /// <summary>
    /// The folders a program lives in, with how much they count (2 = its own install folder, 1 = guessed from its icon
    /// or uninstaller). Never a drive, Windows, Program Files, a game library, a user folder or "Common Files".
    /// </summary>
    private static IEnumerable<(string Folder, int Weight)> FoldersOf(InstalledProgram p)
    {
        var list = new List<(string Folder, int Weight)>();
        if (p.InstallLocation.Length > 3) list.Add((PathUtil.Normalize(p.InstallLocation), 2));
        if (!p.IsStoreApp && DependencyMap.ProgramFolder(p) is { } folder) list.Add((folder, 1));
        return list.Where(f => f.Folder.Length > 3 && !SuiteDetector.IsLibraryRoot(f.Folder) &&
                               !System.IO.Path.GetFileName(f.Folder).Equals("Common Files", StringComparison.OrdinalIgnoreCase) &&
                               SafePaths.IsRemovableFolder(f.Folder, out _))
                   .GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
                   .Select(g => (g.Key, g.Max(x => x.Weight)));
    }

    /// <summary>Does the shortcut's name fit the program it starts? ("Call of Duty" → GameLoop does not.)</summary>
    public static bool Similar(string shortcutName, InstalledProgram p, string target)
    {
        var n = Keywords.Norm(Keywords.CleanName(shortcutName));
        if (n.Length < 3) return true;
        var program = Keywords.Norm(Keywords.CleanName(p.Name));
        var exe = Keywords.Norm(System.IO.Path.GetFileNameWithoutExtension(target));
        var folder = Keywords.Norm(System.IO.Path.GetFileName(p.InstallLocation.TrimEnd('\\')));
        if (Related(program, n) || Related(exe, n) || Related(folder, n)) return true;
        var first = Keywords.CleanName(p.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length >= 4 && n.Contains(Keywords.Norm(first));
    }

    /// <summary>Only an exact name, or the one program whose name starts with it as whole words ("Word" ≠ "WordWeb").</summary>
    private static FoundTarget? ByName(string name, IReadOnlyList<InstalledProgram> live)
    {
        var clean = Keywords.CleanName(name).Trim();
        var n = Keywords.Norm(clean);
        // Protected words ("microsoftedge", "steam") only guard leftover scans – an exact program name is still that program.
        if (n.Length < 4 || Keywords.StopWords.Contains(n)) return null;
        var exact = live.Where(p => Keywords.Norm(Keywords.CleanName(p.Name)) == n).ToList();
        if (exact.Count == 1) return new FoundTarget(FoundKind.Program, exact[0], name);
        if (exact.Count > 1) return new FoundTarget(FoundKind.Ambiguous, null, name);
        var starts = live.Where(p => Keywords.CleanName(p.Name).StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase)).ToList();
        return starts.Count == 1 ? new FoundTarget(FoundKind.Program, starts[0], name) : null;
    }

    private static FoundTarget FromUrl(string url, string name, IReadOnlyList<InstalledProgram> live)
    {
        if (SteamUrl().Match(url) is { Success: true } steam && BySteamId(steam.Groups[1].Value, live) is { } game)
            return new FoundTarget(FoundKind.Program, game, name);
        if (EpicUrl().Match(url) is { Success: true } epic)
        {
            var id = Uri.UnescapeDataString(epic.Groups[1].Value).Split(':').Last();
            if (live.FirstOrDefault(p => p.RegistryKey.Equals("epic:" + id, StringComparison.OrdinalIgnoreCase)) is { } epicGame)
                return new FoundTarget(FoundKind.Program, epicGame, name);
        }
        return ByName(name, live) ?? new FoundTarget(FoundKind.NotFound, null, name);
    }

    /// <summary>Steam registers each game as "Steam App &lt;id&gt;".</summary>
    private static InstalledProgram? BySteamId(string id, IReadOnlyList<InstalledProgram> live) =>
        live.FirstOrDefault(p => p.RegistryKey.EndsWith(@"\Steam App " + id, StringComparison.OrdinalIgnoreCase));

    /// <summary>steam.exe -applaunch 1234 → "1234".</summary>
    private static string? SteamApp(string target, string args) =>
        System.IO.Path.GetFileName(target).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) &&
        SteamArgs().Match(args) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The real path behind junctions and links anywhere in it (D:\Link\app.exe → C:\Program Files\App\app.exe).</summary>
    private static string FinalPath(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // share all, OPEN_EXISTING, BACKUP_SEMANTICS
            if (handle.IsInvalid) return path;
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return path;
            var final = buffer.ToString();
            // A network path stays a network path (\\server\share) – Canonical then refuses it.
            return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
                 : final.StartsWith(@"\\?\") ? final[4..] : final;
        }
        catch (Exception) { return path; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, uint flags);

    [GeneratedRegex(@"\\Installer\\(\{[0-9A-Fa-f\-]{36}\})\\", RegexOptions.IgnoreCase)]
    private static partial Regex MsiProduct();

    [GeneratedRegex(@"^steam://(?:rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamUrl();

    [GeneratedRegex(@"-applaunch\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamArgs();

    [GeneratedRegex(@"^com\.epicgames\.launcher://apps/([^?/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpicUrl();
}
