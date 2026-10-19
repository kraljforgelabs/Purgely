using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// Finds what a program left behind and scores every item by the evidence that links it to the program.
/// </summary>
public sealed partial class LeftoverScanner
{
    private readonly record struct NameMatch(int Points, string Why);

    private readonly ScanTarget _t;
    private readonly IReadOnlyList<InstalledProgram> _all;
    private readonly Dictionary<string, LeftoverItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _roots;
    private readonly HashSet<string> _desktopRoots;
    private readonly HashSet<string> _startRoots;
    private readonly HashSet<string> _blockedFolders;

    private LeftoverScanner(ScanTarget target, IReadOnlyList<InstalledProgram> all)
    {
        _t = target;
        _all = all;
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);

        _desktopRoots = Set(F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory));
        _startRoots = Set(F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms),
                          F(Environment.SpecialFolder.Startup), F(Environment.SpecialFolder.CommonStartup));
        _roots = new[]
        {
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            local, Path.Combine(local, "Programs"), Path.Combine(profile, "AppData", "LocalLow"),
        }.Concat(_startRoots).Concat(_desktopRoots)
         .Where(r => r.Length > 0 && Directory.Exists(r))
         .Select(PathUtil.Normalize)
         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _blockedFolders = Set(_roots.Concat(new[]
        {
            profile, F(Environment.SpecialFolder.CommonDocuments), PathUtil.WindowsDir,
            PathUtil.Normalize(AppContext.BaseDirectory), Cleaner.UndoRoot,
        }).ToArray());
    }

    private static HashSet<string> Set(params string[] paths) =>
        new(paths.Where(p => p.Length > 0).Select(PathUtil.Normalize), StringComparer.OrdinalIgnoreCase);

    public static List<LeftoverItem> Scan(ScanTarget target, IReadOnlyList<InstalledProgram> all)
    {
        var s = new LeftoverScanner(target, all);
        s.Run();
        return s._items.Values
            .Where(i => i.Score >= 25)
            .OrderBy(i => i.IsShared)
            .ThenByDescending(i => i.Score)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Run()
    {
        ScanInstallLocation();
        ScanUninstallKey();
        ScanFolders();
        ScanOtherDrives();
        ScanRegistry();
        ScanKnownRules();

        // Only folders that clearly belong to this program may pull services, tasks and startup
        // entries in (council: a weak "Logitech" or "Acer" folder must never do that).
        var folders = _items.Values
            .Where(i => i.Kind == LeftoverKind.Folder && i.Score >= 55 && !IsVendorFolder(i.Location))
            .Select(i => i.Location).ToList();
        var loc = _t.InstallLocation.Trim('"', ' ');
        if (loc.Length > 3 && IsSafeFolder(loc) && !IsVendorFolder(loc)) folders.Add(PathUtil.Normalize(loc));
        folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        ScanServices(folders);
        ScanTasks(folders);
        ScanStartup(folders);
        AddLinkEvidence();
        AddPublisherAndTimeEvidence();
        MarkShared();

        // A program that is still installed has no "leftovers" yet – never pre-select its parts.
        foreach (var item in _items.Values)
        {
            // A folder named after the company (C:\Program Files\Autodesk, AppData\Roaming\Autodesk) can hold other
            // products and their settings – it is offered, never ticked on its own (brand word = weak evidence).
            var vendorFolder = item.Kind == LeftoverKind.Folder && !item.IsShared && IsVendorFolder(item.Location);
            if (vendorFolder)
                item.AddEvidence(0, $"Company folder ({Path.GetFileName(item.Location.TrimEnd('\\'))}) – tick it only if you use no other program from this company");
            item.Recommended = !vendorFolder && !_t.StillInstalled && !item.IsShared && item.Score >= 70 && !NeverPreselect(item) &&
                               !_userData.Contains(item.Location) &&
                               // A portable program keeps its data inside its own folder (KeePass database, portable browser profile).
                               !(item.Kind == LeftoverKind.Folder &&
                                 _t.UninstallKey?.StartsWith("portable:", StringComparison.OrdinalIgnoreCase) == true);
            item.IsChecked = item.Recommended;
            if (item.Score >= 25) item.SizeBytes = Sizes.Of(item);
        }
    }

    /// <summary>A folder named after a company, or one holding other programs, is never used as proof.</summary>
    private bool IsVendorFolder(string folder)
    {
        var last = Keywords.Norm(Path.GetFileName(folder.TrimEnd('\\')));
        if (_t.Publishers.Contains(last) || AllPublishers.Contains(last)) return true;
        var location = PathUtil.Normalize(folder);
        return _all.Any(p => p.InstallLocation.Length > 3 &&
                             !string.Equals(p.RegistryKey, _t.UninstallKey, StringComparison.OrdinalIgnoreCase) &&
                             PathUtil.IsUnder(PathUtil.Normalize(p.InstallLocation), location));
    }

    private HashSet<string>? _allPublishers;
    private HashSet<string> AllPublishers => _allPublishers ??= new HashSet<string>(
        _all.Select(p => Keywords.CleanPublisher(p.Publisher)).Where(p => p.Length >= 3), StringComparer.OrdinalIgnoreCase);

    /// <summary>Things that are offered but never ticked automatically.</summary>
    private static bool NeverPreselect(LeftoverItem item)
    {
        switch (item.Kind)
        {
            case LeftoverKind.Folder:
                return SafePaths.IsShallow(item.Location);
            case LeftoverKind.Service or LeftoverKind.Driver:
                if (item.BootStart) return true; // boot/system drivers: removing one can stop Windows from starting
                var exe = item.TargetPath;
                return exe.Length == 0 || PathUtil.IsUnder(exe, PathUtil.WindowsDir) ||
                       Path.GetFileName(exe).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
            case LeftoverKind.ScheduledTask or LeftoverKind.StartupEntry:
                return item.TargetPath.Length > 0 && PathUtil.IsUnder(item.TargetPath, PathUtil.WindowsDir);
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ matching
    private NameMatch? MatchName(string candidate)
    {
        var c = Keywords.Norm(candidate);
        if (c.Length == 0 || Keywords.Protected.Contains(c)) return null;
        if (_t.Strong.Contains(c)) return new(60, "Name matches the program");
        if (_t.Strong.Any(k => k.Length >= 5 && c.Contains(k))) return new(45, "Name contains the program name");
        if (_t.Weak.Any(k => c == k || (k.Length >= 5 && c.Contains(k)))) return new(25, "Name partly matches the program");
        return null;
    }

    private NameMatch? MatchNested(string candidate)
    {
        var c = Keywords.Norm(candidate);
        if (c.Length == 0 || Keywords.Protected.Contains(c)) return null;
        return _t.Nested.Any(k => c == k || (k.Length >= 5 && c.Contains(k)))
            ? new(55, "In the publisher's folder and matches the program")
            : null;
    }

    private NameMatch? MatchDeep(string candidate)
    {
        var c = Keywords.Norm(candidate);
        if (c.Length == 0 || Keywords.Protected.Contains(c)) return null;
        if (_t.Strong.Contains(c)) return new(55, "Name matches the program");
        if (_t.Strong.Any(k => k.Length >= 6 && c.Contains(k))) return new(35, "Name contains the program name");
        return null;
    }

    /// <summary>Strict match used for services, tasks and startup entries.</summary>
    private bool MatchStrict(string candidate)
    {
        var c = Keywords.Norm(candidate);
        if (c.Length < 3 || Keywords.Protected.Contains(c)) return false;
        return _t.Strong.Contains(c) || _t.Strong.Any(k => k.Length >= 6 && c.Contains(k));
    }

    // ------------------------------------------------------------------ item helpers
    private LeftoverItem Get(LeftoverKind kind, string location)
    {
        var key = $"{kind}|{location}";
        if (!_items.TryGetValue(key, out var item))
        {
            item = new LeftoverItem { Kind = kind, Location = location };
            _items[key] = item;
        }
        return item;
    }

    private bool IsSafeFolder(string path)
    {
        var p = PathUtil.Normalize(path);
        if (_blockedFolders.Contains(p)) return false;
        return SafePaths.IsRemovableFolder(p, out _);
    }

    private void AddFolder(string path, NameMatch m)
    {
        if (!IsSafeFolder(path)) return;
        Get(LeftoverKind.Folder, PathUtil.Normalize(path)).AddEvidence(m.Points, m.Why);
    }

    private void AddShortcut(string path, NameMatch m)
    {
        if (!SafePaths.IsRemovableFile(path, out _)) return;
        Get(LeftoverKind.Shortcut, path).AddEvidence(m.Points, m.Why);
    }

    private static bool IsLink(string file) =>
        file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ sources
    private void ScanInstallLocation()
    {
        var loc = _t.InstallLocation.Trim('"', ' ');
        if (loc.Length <= 3 || !Directory.Exists(loc)) return;
        if (_t.StillInstalled) { AddFolder(loc, new(45, "The program's install folder (program is still installed)")); return; }
        // A wrong InstallLocation (e.g. the setup's own folder) must not be ticked on its own.
        var nameFits = MatchName(Path.GetFileName(loc.TrimEnd('\\'))) is not null ||
                       MatchNested(Path.GetFileName(loc.TrimEnd('\\'))) is not null;
        AddFolder(loc, nameFits
            ? new(80, "The program's install folder")
            : new(55, "Listed as the install folder, but its name doesn't match the program"));
    }

    /// <summary>
    /// Programs installed outside the usual places (games on D:\Games, repacks): look two levels deep on every
    /// fixed drive for a folder whose name is exactly the program's name. Its size matching what Windows recorded
    /// is strong extra evidence.
    /// </summary>
    private void ScanOtherDrives()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "programfiles", "programfilesx86", "programdata", "users", "recyclebin", "systemvolumeinformation",
            "recovery", "perflogs", "purgelyundo", "documentsandsettings", "msocache", "intel", "amd", "nvidia", "drivers",
        };
        IEnumerable<string> Candidates(string root)
        {
            foreach (var d1 in PathUtil.Dirs(root))
            {
                if (skip.Contains(Keywords.Norm(Path.GetFileName(d1))) || FileMover.IsReparsePoint(d1)) continue;
                yield return d1;
                foreach (var d2 in PathUtil.Dirs(d1))
                    if (!FileMover.IsReparsePoint(d2)) yield return d2;
            }
        }

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToArray(); }
        catch (Exception) { return; }
        foreach (var drive in drives)
        {
            foreach (var dir in Candidates(drive.RootDirectory.FullName))
            {
                if (MatchName(Path.GetFileName(dir)) is not { Points: >= 60 }) continue; // exact name only
                if (!IsSafeFolder(dir)) continue;
                var item = Get(LeftoverKind.Folder, PathUtil.Normalize(dir));
                item.AddEvidence(60, $"Name matches the program (found on {drive.Name.TrimEnd('\\')})");
                if (_t.ExpectedSizeKb is long kb && kb > 1024)
                {
                    var bytes = FileMover.SizeOf(dir);
                    var expected = kb * 1024.0;
                    if (bytes > 0 && Math.Abs(bytes - expected) / expected <= 0.15)
                        item.AddEvidence(25, $"Its size matches what Windows recorded for the program ({Sizes.Format(bytes)})");
                }
            }
        }
    }

    private void ScanUninstallKey()
    {
        if (_t.UninstallKey is null || !RegistryPath.Exists(_t.UninstallKey)) return;
        Get(LeftoverKind.RegistryKey, _t.UninstallKey).AddEvidence(
            _t.StillInstalled ? 40 : 90,
            _t.StillInstalled ? "The program's uninstall entry" : "Windows still lists the program, but it is gone");
    }

    private void ScanFolders()
    {
        foreach (var root in _roots)
        {
            var isDesktop = _desktopRoots.Contains(root);
            var isStart = _startRoots.Contains(root);

            if (isDesktop || isStart)
                foreach (var file in PathUtil.Files(root).Where(IsLink))
                    if (MatchName(Path.GetFileNameWithoutExtension(file)) is { } fm)
                        AddShortcut(file, fm);
            if (isDesktop) continue; // folders on the desktop belong to the user

            foreach (var dir in PathUtil.Dirs(root))
            {
                var name = Path.GetFileName(dir);
                var m = MatchName(name);
                if (m is { } found) AddFolder(dir, found);
                if (m is { Points: >= 60 }) continue;

                var n = Keywords.Norm(name);
                if (_t.Publishers.Contains(n))
                {
                    foreach (var sub in PathUtil.Dirs(dir))
                        if (MatchNested(Path.GetFileName(sub)) is { } nm) AddFolder(sub, nm);
                    if (isStart)
                        foreach (var f in PathUtil.Files(dir).Where(IsLink))
                            if (MatchNested(Path.GetFileNameWithoutExtension(f)) is { } nm) AddShortcut(f, nm);
                }
                else if (!Keywords.Protected.Contains(n))
                {
                    foreach (var sub in PathUtil.Dirs(dir))
                        if (MatchDeep(Path.GetFileName(sub)) is { } dm) AddFolder(sub, dm);
                    if (isStart)
                        foreach (var f in PathUtil.Files(dir).Where(IsLink))
                            if (MatchDeep(Path.GetFileNameWithoutExtension(f)) is { } dm) AddShortcut(f, dm);
                }
            }
        }
    }

    private static readonly (RegistryHive Hive, string Sub, string Display)[] SoftwareRoots =
    {
        (RegistryHive.LocalMachine, "SOFTWARE", @"HKLM\SOFTWARE"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node", @"HKLM\SOFTWARE\WOW6432Node"),
        (RegistryHive.CurrentUser, "Software", @"HKCU\Software"),
    };

    private void ScanRegistry()
    {
        foreach (var (hive, sub, display) in SoftwareRoots)
        {
            foreach (var name in RegistryPath.SubKeys(hive, sub))
            {
                var full = $@"{display}\{name}";
                var m = MatchName(name);
                if (m is { } found) Get(LeftoverKind.RegistryKey, full).AddEvidence(found.Points, found.Why);
                if (m is { Points: >= 60 }) continue;

                var n = Keywords.Norm(name);
                if (_t.Publishers.Contains(n))
                {
                    foreach (var child in RegistryPath.SubKeys(hive, $@"{sub}\{name}"))
                        if (MatchNested(child) is { } nm)
                            Get(LeftoverKind.RegistryKey, $@"{full}\{child}").AddEvidence(nm.Points, nm.Why);
                }
                else if (!Keywords.Protected.Contains(n))
                {
                    foreach (var child in RegistryPath.SubKeys(hive, $@"{sub}\{name}"))
                        if (MatchDeep(child) is { } dm)
                            Get(LeftoverKind.RegistryKey, $@"{full}\{child}").AddEvidence(dm.Points, dm.Why);
                }
            }
        }
    }

    /// <summary>Your settings and saves found by a known-program rule: offered, never ticked.</summary>
    private readonly HashSet<string> _userData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Known places of popular programs (OneDrive, Teams, Chrome, Steam…), checked by hand. Skipped when another
    /// installed program matches the same rule (e.g. a second Chrome channel still uses the folder).
    /// </summary>
    private void ScanKnownRules()
    {
        var rule = KnownPrograms.For(_t.DisplayName);
        if (rule is null) return;
        var othersUseIt = _all.Any(p => !p.IsOrphan && !p.IsRemovedEarlier && rule.Name.IsMatch(p.Name.Trim()) &&
                                        !string.Equals(p.RegistryKey, _t.UninstallKey, StringComparison.OrdinalIgnoreCase));
        if (othersUseIt) return;

        void Offer(string raw, bool userData)
        {
            var path = KnownPrograms.Expand(raw);
            if (path.Length == 0) return;
            var why = userData ? $"Your {rule.Program} settings/data (known location) – remove only if you don't need them"
                               : $"Known location of {rule.Program}";
            var points = userData ? 60 : 85;
            if (path.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
            {
                if (!RegistryPath.Exists(path) || Cleaner.IsCriticalKey(path)) return;
                Get(LeftoverKind.RegistryKey, path).AddEvidence(points, why);
                if (userData) _userData.Add(path);
                return;
            }
            if (!Directory.Exists(path) || !IsSafeFolder(path)) return;
            var key = PathUtil.Normalize(path);
            Get(LeftoverKind.Folder, key).AddEvidence(points, why);
            if (userData) _userData.Add(key);
        }
        foreach (var p in rule.ProgramPaths) Offer(p, false);
        foreach (var p in rule.UserDataPaths) Offer(p, true);
    }

    private void ScanServices(List<string> folders)
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
                    var image = key?.GetValue("ImagePath")?.ToString() ?? "";
                    if (key is null || image.Length == 0) continue;

                    var type = key.GetValue("Type") is int t ? t : 0;
                    var start = key.GetValue("Start") is int st ? st : 3;
                    var kind = (type & 0x3) != 0 ? LeftoverKind.Driver : LeftoverKind.Service;
                    var displayName = key.GetValue("DisplayName")?.ToString() ?? "";
                    if (displayName.StartsWith('@')) displayName = "";
                    var exe = PathUtil.ExtractExecutable(image);

                    LeftoverItem? item = null;
                    var folder = folders.FirstOrDefault(f => PathUtil.IsUnder(exe, f));
                    if (folder is not null)
                    {
                        item = Get(kind, name);
                        item.AddEvidence(75, $"Runs from a leftover folder ({Path.GetFileName(folder)})");
                    }
                    else if (MatchStrict(name) || (displayName.Length > 0 && MatchStrict(displayName)))
                    {
                        item = Get(kind, name);
                        item.AddEvidence(50, $"{(kind == LeftoverKind.Driver ? "Driver" : "Service")} name matches the program");
                    }
                    if (item is null) continue;

                    item.Detail = displayName.Length > 0 ? displayName : name;
                    item.TargetPath = exe;
                    item.BootStart = start <= 1;
                    AddZombieEvidence(item, exe);
                }
                catch (Exception) { /* unreadable service – skip */ }
            }
        }
        catch (Exception) { }
    }

    private void ScanTasks(List<string> folders)
    {
        var root = TaskStore.Root;
        foreach (var file in TaskStore.AllTaskFiles())
        {
            var taskPath = file[root.Length..]; // "\Vendor\TaskName"
            if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;

            string xml;
            try { xml = File.ReadAllText(file); }
            catch (Exception) { continue; }

            var exe = PathUtil.ExtractExecutable(CommandRegex().Match(xml).Groups[1].Value);
            var taskName = Path.GetFileName(file);
            LeftoverItem? item = null;

            var folder = folders.FirstOrDefault(f => PathUtil.IsUnder(exe, f));
            if (folder is not null)
            {
                item = Get(LeftoverKind.ScheduledTask, taskPath);
                item.AddEvidence(75, $"Starts a program from a leftover folder ({Path.GetFileName(folder)})");
            }
            else if (MatchStrict(taskName))
            {
                item = Get(LeftoverKind.ScheduledTask, taskPath);
                item.AddEvidence(50, "Task name matches the program");
            }
            if (item is null) continue;

            item.Detail = taskName;
            item.TargetPath = exe;
            AddZombieEvidence(item, exe);
        }
    }

    private static readonly (RegistryHive Hive, string Sub, string Display)[] RunKeys =
    {
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
    };

    private void ScanStartup(List<string> folders)
    {
        foreach (var (hive, sub, display) in RunKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    var exe = PathUtil.ExtractExecutable(key.GetValue(valueName)?.ToString() ?? "");
                    LeftoverItem? item = null;
                    var folder = folders.FirstOrDefault(f => PathUtil.IsUnder(exe, f));
                    if (folder is not null)
                    {
                        item = Get(LeftoverKind.StartupEntry, $@"{display}\{valueName}");
                        item.AddEvidence(75, $"Starts a program from a leftover folder ({Path.GetFileName(folder)})");
                    }
                    else if (MatchStrict(valueName))
                    {
                        item = Get(LeftoverKind.StartupEntry, $@"{display}\{valueName}");
                        item.AddEvidence(50, "Startup entry name matches the program");
                    }
                    if (item is null) continue;

                    item.RegistryKeyPath = display;
                    item.ValueName = valueName;
                    item.TargetPath = exe;
                    AddZombieEvidence(item, exe);
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>Zombie Hunter: auto-start mechanisms that can bring a removed program back.</summary>
    private void AddZombieEvidence(LeftoverItem item, string exe)
    {
        if (exe.Length > 0 && Path.IsPathRooted(exe) && !File.Exists(exe))
            item.AddEvidence(20, "The program it starts no longer exists");
        if (_t.StillInstalled) return;
        item.IsZombie = true;
        item.AddEvidence(5, "Can bring the program back or keep parts of it running");
    }

    // ------------------------------------------------------------------ extra evidence
    /// <summary>A folder that services, tasks or startup entries of this program run from is strong evidence.</summary>
    private void AddLinkEvidence()
    {
        var linked = _items.Values
            .Where(i => i.Kind is LeftoverKind.Service or LeftoverKind.Driver or LeftoverKind.ScheduledTask
                        or LeftoverKind.StartupEntry && i.TargetPath.Length > 0)
            .ToList();
        foreach (var folder in _items.Values.Where(i => i.Kind == LeftoverKind.Folder).ToList())
        {
            var count = linked.Count(l => PathUtil.IsUnder(l.TargetPath, folder.Location));
            if (count > 0)
                folder.AddEvidence(30, $"{count} other leftover(s) run from this folder");
        }
    }

    private void AddPublisherAndTimeEvidence()
    {
        foreach (var item in _items.Values.Where(i => i.Kind == LeftoverKind.Folder))
        {
            if (_t.Publishers.Count > 0)
            {
                var company = FindCompany(item.Location);
                if (company is not null)
                    item.AddEvidence(20, $"Its files are made by {company}");
            }

            if (_t.InstallDate is { } installed)
            {
                try
                {
                    var created = Directory.GetCreationTime(item.Location).Date;
                    if (Math.Abs((created - installed.Date).TotalDays) <= 1)
                        item.AddEvidence(15, "Created on the day the program was installed");
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>Looks at a few .exe/.dll files for the publisher's company name.</summary>
    private string? FindCompany(string folder)
    {
        var files = PathUtil.Files(folder, "*.exe").Concat(PathUtil.Files(folder, "*.dll"))
            .Concat(PathUtil.Dirs(folder).Take(10).SelectMany(d => PathUtil.Files(d, "*.exe")))
            .Take(25);
        foreach (var file in files)
        {
            try
            {
                var company = FileVersionInfo.GetVersionInfo(file).CompanyName ?? "";
                var norm = Keywords.CleanPublisher(company);
                if (norm.Length >= 3 && _t.Publishers.Any(p => norm.Contains(p) || p.Contains(norm)))
                    return company.Trim();
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>Protects anything another installed program still uses.</summary>
    private void MarkShared()
    {
        var others = _all.Where(p => !p.IsOrphan &&
                                     !string.Equals(p.RegistryKey, _t.UninstallKey, StringComparison.OrdinalIgnoreCase))
                         .ToList();
        var locations = others
            .Where(p => p.InstallLocation.Length > 3 && Path.IsPathRooted(p.InstallLocation))
            .Select(p => (Location: PathUtil.Normalize(p.InstallLocation), p.Name))
            .ToList();
        var publisherUsers = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in others)
        {
            var pub = Keywords.CleanPublisher(o.Publisher);
            if (pub.Length < 3) continue;
            if (!publisherUsers.TryGetValue(pub, out var set)) publisherUsers[pub] = set = new();
            set.Add(o.Name);
        }

        foreach (var item in _items.Values)
        {
            var users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            switch (item.Kind)
            {
                case LeftoverKind.Folder:
                    foreach (var (loc, name) in locations)
                        if (PathUtil.SameOrUnder(loc, item.Location) || PathUtil.IsUnder(item.Location, loc))
                            users.Add(name);
                    break;
                case LeftoverKind.Service or LeftoverKind.Driver or LeftoverKind.ScheduledTask or LeftoverKind.StartupEntry:
                    foreach (var (loc, name) in locations)
                        if (PathUtil.IsUnder(item.TargetPath, loc)) users.Add(name);
                    // Made by a company that still has other programs installed → could be theirs.
                    var company = CompanyOf(item.TargetPath);
                    if (company.Length >= 3)
                        foreach (var (pub, names) in publisherUsers)
                            if (company.Contains(pub) || pub.Contains(company)) users.UnionWith(names);
                    break;
            }

            if (item.Kind is LeftoverKind.Folder or LeftoverKind.RegistryKey)
            {
                var last = Keywords.Norm(item.Location.TrimEnd('\\').Split('\\').Last());
                if (publisherUsers.TryGetValue(last, out var pubUsers)) users.UnionWith(pubUsers);
            }

            if (users.Count > 0)
                item.SharedWith = string.Join(", ", users.Take(3)) + (users.Count > 3 ? $" and {users.Count - 3} more" : "");
        }
    }

    private static string CompanyOf(string exe)
    {
        try
        {
            if (exe.Length == 0 || !File.Exists(exe)) return "";
            return Keywords.CleanPublisher(FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "");
        }
        catch (Exception) { return ""; }
    }

    [GeneratedRegex(@"<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CommandRegex();
}
