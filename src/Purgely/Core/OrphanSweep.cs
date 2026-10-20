using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Purgely.Core.Startup;

namespace Purgely.Core;

/// <summary>
/// "Sweep for orphaned leftovers": traces of programs removed long ago, found without knowing the program.
/// Only things that are provably dead are ticked – a shortcut/startup entry/task/service whose program file is
/// gone, or a folder that is completely empty. Folders that merely don't match any installed program are only
/// offered (unticked), and never in AppData, where games and portable apps keep your settings and saves.
/// </summary>
public static class OrphanSweep
{
    public static List<LeftoverItem> Scan(IReadOnlyList<InstalledProgram> programs, IProgress<string>? progress = null)
    {
        // Orphan detection is a heuristic too – programs flagged as orphans still protect their names and folders.
        var installed = programs.Where(p => !p.IsRemovedEarlier).ToList();
        var used = new UsedNames(installed);
        var items = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        LeftoverItem Add(LeftoverKind kind, string location, int points, string why)
        {
            var key = $"{kind}|{location}";
            if (!items.TryGetValue(key, out var item)) items[key] = item = new LeftoverItem { Kind = kind, Location = location };
            item.AddEvidence(points, why);
            return item;
        }

        var live = new List<string>(); // programs that shortcuts, startup entries, tasks and services still start
        progress?.Report("Checking shortcuts…");
        BrokenShortcuts(Add, live);
        progress?.Report("Checking startup entries…");
        DeadRunValues(Add, live);
        progress?.Report("Checking scheduled tasks…");
        DeadTasks(Add, live);
        progress?.Report("Checking services…");
        DeadServices(Add, live);
        progress?.Report("Looking for empty and unused folders…");
        Folders(Add, used, installed, live);

        foreach (var item in items.Values)
        {
            item.Recommended = item.Score >= 70 && !item.IsShared;
            item.IsChecked = item.Recommended;
            item.SizeBytes = Sizes.Of(item);
        }
        return items.Values.OrderByDescending(i => i.Recommended).ThenBy(i => i.Kind)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate LeftoverItem AddFn(LeftoverKind kind, string location, int points, string why);

    // ---------------------------------------------------------------- dead links

    /// <summary>A program file that is really gone: a local path on a fixed drive that exists, outside Windows.</summary>
    private static readonly string SystemDrive = Path.GetPathRoot(PathUtil.WindowsDir) ?? @"C:\";

    /// <summary>Dead = the file is gone. Returns the points to give (0 = alive or can't tell).</summary>
    private static int DeadPoints(string raw, string exe, int points)
    {
        if (raw.Contains('%')) return 0;          // per-user or machine variable – judged for the wrong account; skip
        if (exe.Contains('?')) return 0;          // characters lost in conversion – can't tell
        if (!IsDeadTarget(exe)) return 0;
        // On another drive (a USB disk can look "fixed" and change letters): offer only.
        return exe.StartsWith(SystemDrive, StringComparison.OrdinalIgnoreCase) ? points : 50;
    }

    private static bool IsDeadTarget(string exe)
    {
        if (exe.Length < 4 || !Path.IsPathRooted(exe) || exe.StartsWith(@"\\")) return false;
        if (PathUtil.IsUnder(exe, PathUtil.WindowsDir)) return false;  // Windows files: not ours to judge
        if (exe.Contains('%')) return false;                             // unexpanded variable – can't tell
        foreach (var skip in MicrosoftOwned)
            if (exe.Contains(skip, StringComparison.OrdinalIgnoreCase)) return false; // Store apps, Defender…
        try
        {
            var root = Path.GetPathRoot(exe);
            if (root is null) return false;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false; // USB/network drive may be unplugged
        }
        catch (Exception) { return false; }
        if (File.Exists(exe) || Directory.Exists(exe)) return false;
        // A 64-bit process may resolve "Program Files" where the shortcut meant "(x86)" – probe the other one too.
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var pf = F(Environment.SpecialFolder.ProgramFiles);
        var pf86 = F(Environment.SpecialFolder.ProgramFilesX86);
        if (pf.Length > 0 && pf86.Length > 0)
        {
            var other = PathUtil.IsUnder(exe, pf) ? pf86 + exe[pf.Length..] : PathUtil.IsUnder(exe, pf86) ? pf + exe[pf86.Length..] : null;
            if (other is not null && (File.Exists(other) || Directory.Exists(other))) return false;
        }
        return true;
    }

    private static readonly string[] MicrosoftOwned =
    {
        @"\WindowsApps\", @"\ProgramData\Microsoft\", @"\Program Files\Windows", @"\Program Files (x86)\Windows",
        @"\Microsoft\WindowsApps\",
    };

    private static void BrokenShortcuts(AddFn add, List<string> live)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        foreach (var root in new[] { F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms),
                                     F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            foreach (var lnk in FileMover.WalkFiles(root).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                var target = StartupScanner.ResolveShortcut(lnk);
                if (target is null) continue;
                var points = DeadPoints("", target, 85);
                if (points == 0) { live.Add(target); continue; }
                if (!SafePaths.IsRemovableFile(lnk, out _)) continue;
                var item = add(LeftoverKind.Shortcut, lnk, points, "Shortcut to a program that no longer exists");
                item.TargetPath = target;
            }
        }
    }

    private static readonly (RegistryHive Hive, string Sub, string Display)[] RunKeys =
    {
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
    };

    private static void DeadRunValues(AddFn add, List<string> live)
    {
        foreach (var (hive, sub, display) in RunKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                    var exe = PathUtil.ExtractExecutable(raw);
                    var points = DeadPoints(raw, exe, 85);
                    if (points == 0) { live.Add(exe); continue; }
                    var item = add(LeftoverKind.StartupEntry, $@"{display}\{name}", points, "Starts a program that no longer exists");
                    item.RegistryKeyPath = display;
                    item.ValueName = name;
                    item.TargetPath = exe;
                    item.Detail = name;
                }
            }
            catch (Exception) { }
        }
    }

    private static void DeadTasks(AddFn add, List<string> live)
    {
        var root = TaskStore.Root;
        foreach (var file in TaskStore.AllTaskFiles())
        {
            var taskPath = file[root.Length..];
            if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            string xml;
            try { xml = File.ReadAllText(file); } catch (Exception) { continue; }
            var commands = Regex.Matches(xml, "<Command>(.*?)</Command>", RegexOptions.Singleline);
            if (commands.Count != 1) continue; // several actions: only judge simple tasks
            var raw = System.Net.WebUtility.HtmlDecode(commands[0].Groups[1].Value.Trim());
            var exe = PathUtil.ExtractExecutable(raw);
            if (!BelongsToCurrentUserOrSystem(xml)) { live.Add(exe); continue; } // another user's task: never judged
            var points = DeadPoints(raw, exe, 80);
            if (points == 0) { live.Add(exe); continue; }
            var item = add(LeftoverKind.ScheduledTask, taskPath, points, "Scheduled task that starts a program that no longer exists");
            item.TargetPath = exe;
            item.Detail = Path.GetFileName(file);
        }
    }

    private static void DeadServices(AddFn add, List<string> live)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var key = services.OpenSubKey(name);
                    var image = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                    if (key is null || image.Length == 0) continue;
                    var type = key.GetValue("Type") is int t ? t : 0;
                    var start = key.GetValue("Start") is int s ? s : 3;
                    if ((type & 0x3) != 0 || start <= 1) continue; // drivers and boot/system services: never
                    var exe = PathUtil.ExtractExecutable(image);
                    var points = DeadPoints(image, exe, 70);
                    if (points == 0) { live.Add(exe); continue; }
                    var item = add(LeftoverKind.Service, name, points, "Service whose program file no longer exists");
                    var display = key.GetValue("DisplayName")?.ToString() ?? "";
                    item.Detail = display.Length > 0 && !display.StartsWith('@') ? display : name;
                    item.TargetPath = exe;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    // ---------------------------------------------------------------- folders

    /// <summary>Names and folders that belong to installed programs – a folder matching any of them is not orphaned.</summary>
    private sealed class UsedNames
    {
        private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
        public UsedNames(IEnumerable<InstalledProgram> installed)
        {
            foreach (var p in installed)
            {
                var t = ScanTarget.FromProgram(p, stillInstalled: true);
                _words.UnionWith(t.Strong);
                _words.UnionWith(t.Weak.Where(w => w.Length >= 4));
                _words.UnionWith(t.Publishers.Where(w => w.Length >= 3));
            }
        }
        public bool Matches(string folderName)
        {
            var n = Keywords.Norm(folderName);
            if (n.Length < 3) return true; // too short to judge – treat as used
            return _words.Any(w => n == w || (w.Length >= 4 && (n.Contains(w) || (n.Length >= 4 && w.Contains(n)))));
        }
    }

    private static void Folders(AddFn add, UsedNames used, List<InstalledProgram> installed, List<string> live)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var programFiles = new[] { F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86) };
        var dataRoots = new[] { F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
                                F(Environment.SpecialFolder.LocalApplicationData) };
        var locations = installed.Select(DependencyMap.ProgramFolder).Where(l => !string.IsNullOrEmpty(l))
                                 .Select(l => PathUtil.Normalize(l!)).ToList();
        // Folders still in use: install folders, anything a live shortcut/startup entry/task/service starts,
        // and every folder on the PATH (e.g. an empty %APPDATA%\npm that Node.js needs).
        locations.AddRange(live.Where(l => l.Length > 3 && Path.IsPathRooted(l)).Select(l => PathUtil.Normalize(Path.GetDirectoryName(l) ?? l)));
        locations.AddRange(PathFolders());
        bool HoldsInstalled(string dir) => locations.Any(l => PathUtil.SameOrUnder(l, dir) || PathUtil.IsUnder(dir, l));
        var minAge = DateTime.Now.AddDays(-2); // an installer may be creating it right now

        foreach (var root in programFiles.Concat(dataRoots).Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isProgramFiles = programFiles.Contains(root, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in PathUtil.Dirs(root))
            {
                var name = Path.GetFileName(dir);
                var norm = Keywords.Norm(name);
                if (Keywords.Protected.Contains(norm) || WindowsOwned.Contains(norm) || norm.StartsWith("regid") ||
                    norm.StartsWith("windows") || norm.StartsWith("microsoft") ||
                    FileMover.IsReparsePoint(dir) || IsHiddenOrSystem(dir)) continue;
                if (!SafePaths.IsRemovableFolder(dir, out _) || HoldsInstalled(dir)) continue;
                if (Created(dir) > minAge) continue;

                if (IsEmpty(dir))
                {
                    if (!used.Matches(name)) add(LeftoverKind.Folder, PathUtil.Normalize(dir), 80, "Empty folder that no installed program uses");
                    continue;
                }
                // Not empty: only in Program Files, only offered (never ticked).
                if (isProgramFiles && !used.Matches(name))
                    add(LeftoverKind.Folder, PathUtil.Normalize(dir), 40,
                        "No installed program seems to use it – check what it is before removing");
            }
        }
    }

    /// <summary>
    /// Proven empty: no files and no links at any depth, and every folder could be read. Any error (access
    /// denied…) means "unknown", which is never treated as empty.
    /// </summary>
    private static bool IsEmpty(string dir)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };
        var stack = new Stack<string>();
        stack.Push(dir);
        try
        {
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", options))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) == 0) return false;
                    stack.Push(entry);
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> PathFolders()
    {
        var all = (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "") + ";" +
                  (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "") + ";" +
                  (Environment.GetEnvironmentVariable("PATH") ?? "");
        return all.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(p => Environment.ExpandEnvironmentVariables(p))
                  .Where(p => p.Length > 3 && Path.IsPathRooted(p))
                  .Select(PathUtil.Normalize);
    }

    /// <summary>Tasks run as another user are judged with the wrong profile – only this user's, SYSTEM's or no principal.</summary>
    private static bool BelongsToCurrentUserOrSystem(string xml)
    {
        var m = Regex.Match(xml, "<UserId>(.*?)</UserId>", RegexOptions.Singleline);
        if (!m.Success) return true;
        var user = m.Groups[1].Value.Trim();
        if (user is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return true;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        return user.Equals(sid, StringComparison.OrdinalIgnoreCase) ||
               user.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) ||
               user.EndsWith("\\" + Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Folders Windows, .NET or Visual Studio create themselves – never reported, empty or not.</summary>
    private static readonly HashSet<string> WindowsOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "commonfiles", "windowsdefender", "windowsdefenderadvancedthreatprotection", "windowsmail", "windowsmediaplayer",
        "windowsnt", "windowsphotoviewer", "windowsapps", "windowspowershell", "modifiablewindowsapps", "internetexplorer",
        "uninstallinformation", "referenceassemblies", "msbuild", "dotnet", "packagecache", "packages", "ssh", "usoprivate",
        "usoshared", "softwaredistribution", "windowsholographicdevices", "comms", "crashdumps", "d3dscache",
        "connecteddevicesplatform", "placeholdertilelogofolder", "virtualstore", "temp", "history", "publishers",
        "programs", "applicationdata", "templates", "desktop", "documents", "startmenu", "favorites", "fonts",
        "iconcache", "peernetworking", "elevateddiagnostics", "deliveryoptimization", "regid19910", "nvidia", "intel",
        "npm", "npmcache", "nodegyp", "pip", "nuget", "dotnet", "git", "ssh", "gnupg", "kits", "windowskits",
    };

    private static bool IsHiddenOrSystem(string dir)
    {
        try { return (File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch (Exception) { return true; }
    }

    private static DateTime Created(string dir)
    {
        try { return Directory.GetCreationTime(dir); } catch (Exception) { return DateTime.Now; }
    }
}
