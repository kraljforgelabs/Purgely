using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core.Trial;

/// <summary>
/// Trial Mode: snapshot before an install, compare after it, and revert everything on request.
/// </summary>
public static class TrialEngine
{
    public static string Root => Path.Combine(Timeline.DataFolder, "Trials");

    private sealed record HivePart(string Live, string File, string Display);

    /// <summary>Adds "Step x of y" in front of every progress message.</summary>
    private sealed class StepProgress(IProgress<string> inner, string step) : IProgress<string>
    {
        public void Report(string value) => inner.Report($"{step}  ·  {value}");
    }

    private static readonly HivePart[] Hives =
    {
        new(@"HKLM\SOFTWARE", "software.hiv", @"HKLM\SOFTWARE"),
        new(@"HKCU\Software", "user.hiv", @"HKCU\Software"),
        new(@"HKLM\SYSTEM\CurrentControlSet\Services", "services.hiv", @"HKLM\SYSTEM\CurrentControlSet\Services"),
    };

    /// <summary>Windows' own busy areas are skipped – except the places installers really use.</summary>
    private static readonly string[] SoftwareBlocked =
    {
        "Microsoft", @"WOW6432Node\Microsoft", @"Classes\Local Settings", @"WOW6432Node\Classes\Local Settings",
    };

    private static readonly string[] SoftwareAllowed = Expand(new[]
    {
        @"Microsoft\Windows\CurrentVersion\Uninstall", @"Microsoft\Windows\CurrentVersion\Run",
        @"Microsoft\Windows\CurrentVersion\RunOnce", @"Microsoft\Windows\CurrentVersion\App Paths",
        @"Microsoft\Windows\CurrentVersion\Installer", @"Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
        @"Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects",
        @"Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
        @"Microsoft\Windows NT\CurrentVersion\Fonts",
    });

    private static readonly string[] UserBlocked = { "Microsoft", @"Classes\Local Settings" };

    private static readonly string[] UserAllowed =
    {
        @"Microsoft\Windows\CurrentVersion\Run", @"Microsoft\Windows\CurrentVersion\RunOnce",
        @"Microsoft\Windows\CurrentVersion\Uninstall", @"Microsoft\Windows\CurrentVersion\App Paths",
    };

    private static string[] Expand(string[] paths) => paths.Concat(paths.Select(p => $@"WOW6432Node\{p}")).ToArray();

    private static RegistryDiff MakeComparer(int part, TrialDiff diff, IProgress<string>? progress,
                                             DateTime? since = null) => part switch
    {
        0 => new RegistryDiff(Hives[0].Display, diff, k => diff.AddedRegistryKeys.Add(k),
                              SoftwareBlocked, SoftwareAllowed, progress: progress, since: since),
        1 => new RegistryDiff(Hives[1].Display, diff, k => diff.AddedRegistryKeys.Add(k),
                              UserBlocked, UserAllowed, progress: progress, since: since),
        _ => new RegistryDiff(Hives[2].Display, diff, k => diff.AddedServices.Add(k.Split('\\').Last()),
                              compareValues: false, topLevelOnly: true, progress: progress, since: since),
    };

    private static RegistryKey? OpenLive(HivePart hive)
    {
        var (liveHive, liveSub) = RegistryPath.Parse(hive.Live);
        using var liveBase = RegistryKey.OpenBaseKey(liveHive, RegistryView.Registry64);
        return liveBase.OpenSubKey(liveSub);
    }

    private static string SnapshotFile(string folder, HivePart hive) => Path.Combine(folder, hive.File + ".json.gz");

    // ------------------------------------------------------------------ storage
    private static string Folder(TrialRecord r) => Path.Combine(Root, r.Id);

    public static void Save(TrialRecord r)
    {
        Directory.CreateDirectory(Folder(r));
        File.WriteAllText(Path.Combine(Folder(r), "trial.json"),
            JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<TrialRecord> List()
    {
        var list = new List<TrialRecord>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var r = JsonSerializer.Deserialize<TrialRecord>(File.ReadAllText(Path.Combine(dir, "trial.json")));
                if (r is not null) list.Add(r);
            }
            catch (Exception) { }
        }
        return list.OrderByDescending(r => r.Started).ToList();
    }

    public static void Delete(TrialRecord r) => FileMover.DeleteBestEffort(Folder(r), true);

    /// <summary>Removes the big snapshot files once they are no longer needed.</summary>
    private static void DropSnapshots(TrialRecord r)
    {
        foreach (var name in Hives.SelectMany(h => new[] { h.File, h.File + ".json.gz" }).Append("files.gz"))
        {
            try { File.Delete(Path.Combine(Folder(r), name)); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ 1. before
    public static TrialRecord Prepare(string installer, bool restorePoint, IProgress<string> progress)
    {
        var r = new TrialRecord
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Installer = installer,
            Started = DateTime.Now,
            State = TrialState.Installing,
        };
        var folder = Folder(r);
        Directory.CreateDirectory(folder);

        try
        {
            if (restorePoint)
            {
                progress.Report("Step 1 of 3  ·  Creating a system restore point… (Windows gives no progress for this – usually under a minute)");
                r.RestorePointCreated = Shell.Run("powershell",
                    "-NoProfile -Command \"Checkpoint-Computer -Description 'Purgely trial' -RestorePointType APPLICATION_INSTALL\"",
                    300_000).Code == 0;
            }

            for (var i = 0; i < Hives.Length; i++)
            {
                var hive = Hives[i];
                progress.Report($"Step 2 of 3  ·  Saving registry, part {i + 1} of {Hives.Length} ({hive.Display})…");
                var hiveFile = Path.Combine(folder, hive.File);
                if (Shell.Run("reg", $"save \"{hive.Live}\" \"{hiveFile}\" /y", 300_000).Code == 0) continue;

                // Windows refused (e.g. volatile keys) – read the branch ourselves instead.
                try { File.Delete(hiveFile); } catch (Exception) { }
                var step = new StepProgress(progress, $"Step 2 of 3  ·  Registry part {i + 1} of {Hives.Length}");
                step.Report($"Reading {hive.Display}…");
                using var live = OpenLive(hive) ?? throw new InvalidOperationException($"Cannot open {hive.Display}");
                MakeComparer(i, new TrialDiff(), step).TakeSnapshot(live).Save(SnapshotFile(folder, hive));
            }

            var filesStep = new StepProgress(progress, "Step 3 of 3");
            filesStep.Report("Recording files…");
            var before = FileSnapshot.Take(filesStep);
            r.FilesBefore = before.Count;
            FileSnapshot.Save(Path.Combine(folder, "files.gz"), before);
        }
        catch (Exception)
        {
            Delete(r);
            throw;
        }

        Save(r);
        Timeline.Log(TimelineEventType.TrialStarted, Path.GetFileName(installer), "Trial install started – Purgely is watching");
        return r;
    }

    public static void RunInstaller(TrialRecord r) =>
        Process.Start(new ProcessStartInfo(r.Installer)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(r.Installer) ?? "",
        });

    // ------------------------------------------------------------------ 2. after
    public static TrialDiff Analyze(TrialRecord r, IProgress<string> progress)
    {
        var folder = Folder(r);
        var diff = new TrialDiff();

        var filesStep = new StepProgress(progress, "Step 1 of 3");
        filesStep.Report("Recording files after the install…");
        var after = FileSnapshot.Take(filesStep, r.FilesBefore);
        progress.Report("Step 2 of 3  ·  Comparing files…");
        FileSnapshot.Compare(FileSnapshot.Load(Path.Combine(folder, "files.gz")), after, diff);

        // Scheduled task files are reverted with schtasks, not as plain files.
        var tasksRoot = TaskStore.Root;
        foreach (var f in diff.AddedFiles.Where(f => PathUtil.IsUnder(f, tasksRoot)).ToList())
        {
            diff.AddedTasks.Add(f[tasksRoot.Length..]);
            diff.AddedFiles.Remove(f);
        }
        foreach (var d in diff.AddedDirectories.Where(d => PathUtil.IsUnder(d, tasksRoot)).ToList())
        {
            diff.AddedTasks.AddRange(PathUtil.Files(d).Select(f => f[tasksRoot.Length..]));
            diff.AddedDirectories.Remove(d);
        }

        for (var i = 0; i < Hives.Length; i++)
        {
            var hive = Hives[i];
            var regStep = new StepProgress(progress, $"Step 3 of 3  ·  Registry part {i + 1} of {Hives.Length}");
            regStep.Report($"Comparing {hive.Display}…");
            var comparer = MakeComparer(i, diff, regStep, r.Started);

            var snapshotFile = SnapshotFile(folder, hive);
            if (File.Exists(snapshotFile))
            {
                using var liveKey = OpenLive(hive);
                if (liveKey is not null) comparer.Compare(liveKey, RegSnapshot.Load(snapshotFile));
                continue;
            }

            var mount = $"Purgely_Trial_{i}";
            Shell.Run("reg", $"unload \"HKLM\\{mount}\""); // in case an earlier run crashed
            var (code, output) = Shell.Run("reg", $"load \"HKLM\\{mount}\" \"{Path.Combine(folder, hive.File)}\"", 300_000);
            if (code != 0) throw new InvalidOperationException($"Could not load the saved registry: {output}");

            try
            {
                using (var live = OpenLive(hive))
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var before = hklm.OpenSubKey(mount))
                {
                    if (live is not null && before is not null) comparer.Compare(live, before);
                }
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (Shell.Run("reg", $"unload \"HKLM\\{mount}\"").Code == 0) break;
                    Thread.Sleep(1000);
                }
            }
        }

        // Which programs did the installer register?
        foreach (var key in diff.AddedRegistryKeys.Where(k => k.Contains(@"\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)))
        {
            using var k = RegistryPath.Open(key);
            var name = k?.GetValue("DisplayName")?.ToString();
            if (string.IsNullOrWhiteSpace(name) || k?.GetValue("SystemComponent") is 1) continue;
            diff.NewPrograms.Add(key);
            diff.NewProgramNames.Add(name.Trim());
        }

        // Bundleware Detector, live: more than one real program from different publishers.
        var publishers = diff.NewPrograms
            .Select(key => { using var k = RegistryPath.Open(key); return (Name: k?.GetValue("DisplayName")?.ToString() ?? "", Pub: Keywords.CleanPublisher(k?.GetValue("Publisher")?.ToString())); })
            .Where(x => !DependencyMap.IsRuntimeName(x.Name) && !x.Pub.StartsWith("microsoft"))
            .Select(x => x.Pub).Distinct().Count();
        diff.PossibleBundle = publishers >= 2;

        r.Diff = diff;
        r.Analyzed = DateTime.Now;
        r.State = TrialState.Analyzed;
        Save(r);
        DropSnapshots(r);
        Timeline.Log(TimelineEventType.TrialStarted, r.Title,
            $"Trial install recorded: {r.ChangesText}" + (diff.PossibleBundle ? " – ⚠ more than one publisher installed" : ""));
        return diff;
    }

    // ------------------------------------------------------------------ 3. keep / revert
    public static void Keep(TrialRecord r)
    {
        r.State = TrialState.Kept;
        Save(r);
        DropSnapshots(r);
        Timeline.Log(TimelineEventType.TrialKept, r.Title, "Trial ended – program kept");
    }

    public static void MarkReverted(TrialRecord r)
    {
        r.State = TrialState.Reverted;
        Save(r);
        Timeline.Log(TimelineEventType.TrialReverted, r.Title, "Trial reverted – the PC was put back as it was");
    }

    /// <summary>
    /// Turns the recorded changes into items the Cleaner can undo (with an Undo package of its own).
    /// Council rule: only what can be tied to the new program is ticked. Everything else that merely
    /// changed during the trial (Windows Update, other apps updating themselves…) is offered unticked.
    /// Nothing inside the Windows folder is ever offered.
    /// </summary>
    public static List<LeftoverItem> BuildRevertItems(TrialRecord r, IReadOnlyList<InstalledProgram> programs)
    {
        var diff = r.Diff ?? throw new InvalidOperationException("This trial has not been analyzed yet.");
        var items = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        LeftoverItem Add(LeftoverKind kind, string location, int points, string why)
        {
            var key = $"{kind}|{location}";
            if (!items.TryGetValue(key, out var item)) items[key] = item = new LeftoverItem { Kind = kind, Location = location };
            item.AddEvidence(points, why);
            return item;
        }

        // ---- what belongs to the new program
        var newKeys = new HashSet<string>(diff.NewPrograms, StringComparer.OrdinalIgnoreCase);
        var newPrograms = programs.Where(p => newKeys.Contains(p.RegistryKey)).ToList();
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in newPrograms)
        {
            var t = ScanTarget.FromProgram(p, stillInstalled: false);
            words.UnionWith(t.Strong);
        }
        if (words.Count == 0)
            foreach (var w in ScanTarget.FromName(Path.GetFileNameWithoutExtension(r.Installer)).Strong) words.Add(w);
        words.RemoveWhere(w => w.Length < 4 || Keywords.Protected.Contains(w));

        bool NameFits(string text)
        {
            var n = Keywords.Norm(text);
            return n.Length >= 3 && words.Any(w => n.Contains(w));
        }

        var addedFiles = new HashSet<string>(diff.AddedFiles.Select(PathUtil.Normalize), StringComparer.OrdinalIgnoreCase);
        var addedDirs = diff.AddedDirectories.Select(PathUtil.Normalize).ToList();
        // Top-level new folders that are clearly the new program's (by name) or its install folder.
        var ownRoots = addedDirs.Where(d => NameFits(Path.GetFileName(d))).ToList();
        ownRoots.AddRange(newPrograms.Where(p => p.InstallLocation.Length > 3).Select(p => PathUtil.Normalize(p.InstallLocation)));
        ownRoots = ownRoots.Where(d => !PathUtil.SameOrUnder(d, PathUtil.WindowsDir) && SafePaths.IsRemovableFolder(d, out _))
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Owned(string path) =>
            path.Length > 0 && (addedFiles.Contains(PathUtil.Normalize(path)) || ownRoots.Any(o => PathUtil.SameOrUnder(path, o)));
        bool UnderOwnRoot(string path) => path.Length > 0 && ownRoots.Any(o => PathUtil.SameOrUnder(path, o));
        bool InWindows(string path) => path.Length > 0 && PathUtil.SameOrUnder(PathUtil.Normalize(path), PathUtil.WindowsDir);

        const int Own = 95, Unsure = 45;
        const string OwnWhy = "Added by the trial install";
        const string UnsureWhy = "Appeared during the trial, but may belong to Windows or another program – check it";

        // ---- services and drivers
        var existingServices = new HashSet<string>(RegistryPath.SubKeys(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var service in diff.AddedServices)
        {
            // Per-user instances Windows creates itself (CDPUserSvc_1a2b3c…).
            var m = System.Text.RegularExpressions.Regex.Match(service, "^(.+)_[0-9a-fA-F]{4,8}$");
            if (m.Success && existingServices.Contains(m.Groups[1].Value)) continue;

            using var key = RegistryPath.Open($@"HKLM\SYSTEM\CurrentControlSet\Services\{service}");
            if (key is null) continue;
            var isDriver = key.GetValue("Type") is int t && (t & 0x3) != 0;
            var exe = PathUtil.ExtractExecutable(key.GetValue("ImagePath")?.ToString() ?? "");
            var own = Owned(exe) && (!InWindows(exe) || addedFiles.Contains(PathUtil.Normalize(exe)));
            if (!own && InWindows(exe)) continue; // Windows' own (svchost, inbox drivers) – never offered
            var item = Add(isDriver ? LeftoverKind.Driver : LeftoverKind.Service, service, own ? Own : Unsure, own ? OwnWhy : UnsureWhy);
            item.Detail = key.GetValue("DisplayName")?.ToString() is { } d && !d.StartsWith('@') ? d : service;
            item.TargetPath = exe;
            item.BootStart = key.GetValue("Start") is int st && st <= 1;
        }

        // ---- scheduled tasks
        foreach (var task in diff.AddedTasks.Where(t => File.Exists(TaskStore.Root + t)))
        {
            if (task.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            string exe = "";
            try
            {
                var xml = File.ReadAllText(TaskStore.Root + task);
                var cmd = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>",
                    System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
                exe = PathUtil.ExtractExecutable(cmd);
            }
            catch (Exception) { }
            var own = Owned(exe) || NameFits(Path.GetFileName(task));
            var item = Add(LeftoverKind.ScheduledTask, task, own ? Own : Unsure, own ? OwnWhy : UnsureWhy);
            item.TargetPath = exe;
        }

        // ---- folders and files (never inside Windows)
        foreach (var dir in addedDirs.Where(Directory.Exists))
        {
            if (InWindows(dir) || !SafePaths.IsRemovableFolder(dir, out _)) continue; // its children are offered instead
            // Only the top removable folder of a new tree is listed; its contents go with it.
            if (addedDirs.Any(o => PathUtil.IsUnder(dir, o) && !InWindows(o) && SafePaths.IsRemovableFolder(o, out _))) continue;
            var own = UnderOwnRoot(dir) || NameFits(Path.GetFileName(dir));
            Add(LeftoverKind.Folder, dir, own ? Own : Unsure, own ? OwnWhy : UnsureWhy);
        }
        foreach (var file in diff.AddedFiles.Where(File.Exists))
        {
            if (InWindows(file)) continue;
            if (addedDirs.Any(o => PathUtil.IsUnder(file, o) && !InWindows(o) && SafePaths.IsRemovableFolder(o, out _)))
                continue; // goes with its new folder
            var isLink = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
            var own = UnderOwnRoot(file) || NameFits(Path.GetFileNameWithoutExtension(file));
            Add(isLink ? LeftoverKind.Shortcut : LeftoverKind.File, file, own ? Own : Unsure, own ? OwnWhy : UnsureWhy);
        }

        // ---- registry keys
        var ownKeys = diff.AddedRegistryKeys.Where(k => newKeys.Contains(k) || NameFits(k.Split('\\').Last())).ToList();
        foreach (var key in diff.AddedRegistryKeys.Where(k => RegistryPath.Exists(k)))
        {
            if (IsStoreAppKey(key)) continue;
            if (Cleaner.IsCriticalKey(key))
            {
                // A structural key (e.g. ...\Uninstall) created for the first time: offer its children instead.
                foreach (var child in SubKeysOf(key))
                {
                    var full = $@"{key}\{child}";
                    if (Cleaner.IsCriticalKey(full) || IsStoreAppKey(full)) continue;
                    var ownChild = newKeys.Contains(full) || NameFits(child);
                    Add(LeftoverKind.RegistryKey, full, ownChild ? Own : Unsure, ownChild ? OwnWhy : UnsureWhy);
                }
                continue;
            }
            if (diff.AddedRegistryKeys.Any(o => !string.Equals(o, key, StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(key, o) && !Cleaner.IsCriticalKey(o)))
                continue; // goes with its new parent key
            var own = ownKeys.Any(o => PathUtil.SameOrUnder(key, o));
            Add(LeftoverKind.RegistryKey, key, own ? Own : Unsure, own ? OwnWhy : UnsureWhy);
        }

        // ---- registry values
        foreach (var change in diff.ValueChanges.Concat(diff.DeletedValues))
        {
            if (IsStoreAppKey(change.Key)) continue;
            var deleted = diff.DeletedValues.Contains(change);
            var action = change.Old is null ? "will be removed" : deleted ? "will be put back" : "will be set back";
            var data = $"{change.New?.Data} {change.Old?.Data}";
            var own = ownKeys.Any(o => PathUtil.SameOrUnder(change.Key, o)) || NameFits(change.Name) ||
                      ownRoots.Any(o => data.Contains(o, StringComparison.OrdinalIgnoreCase)) ||
                      NameFits(change.Key.Split('\\').Last());
            var item = Add(LeftoverKind.RegistryValue, $"{change.Key}\\{change.Name}", own ? Own : Unsure,
                own ? $"{(change.Old is null ? "Added" : deleted ? "Deleted" : "Changed")} by the trial install – {action}"
                    : $"Changed during the trial, maybe by something else – {action}");
            item.RegistryKeyPath = change.Key;
            item.ValueName = change.Name;
            item.OldValue = change.Old;

            // Changed again after the analysis (e.g. you picked a different default app later) → don't tick.
            if (change.New is not null || (change.Old is not null && !deleted))
            {
                try
                {
                    using var k = RegistryPath.Open(change.Key);
                    var now = k is null ? null : RegValue.Read(k, change.Name);
                    if (change.New is not null && (now is null || !now.SameAs(change.New)))
                        item.SharedWith = "a newer setting (it was changed again after the trial)";
                }
                catch (Exception) { }
            }
        }

        // ---- data the program created after the install (first run, settings…) – a normal deep scan.
        foreach (var program in newPrograms)
        {
            foreach (var found in LeftoverScanner.Scan(ScanTarget.FromProgram(program, stillInstalled: false), programs))
            {
                var key = $"{found.Kind}|{found.Location}";
                if (!items.ContainsKey(key) && found.Recommended) items[key] = found;
            }
        }

        // ---- never touch what programs that existed BEFORE the trial use (e.g. a browser that updated meanwhile).
        var existing = programs
            .Where(p => !newKeys.Contains(p.RegistryKey) && !p.IsOrphan)
            .Select(p => (Location: DependencyMap.ProgramFolder(p) ?? "", p.Name))
            .Where(e => e.Location.Length > 3)
            .ToList();
        foreach (var item in items.Values)
        {
            var path = item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut ? item.Location : item.TargetPath;
            if (path.Length == 0 || item.IsShared) continue;
            var owner = existing.FirstOrDefault(e => PathUtil.SameOrUnder(path, e.Location) ||
                                                     (item.Kind == LeftoverKind.Folder && PathUtil.IsUnder(e.Location, path)));
            if (owner.Name is not null) item.SharedWith = owner.Name;
        }

        foreach (var item in items.Values)
        {
            item.Recommended = !item.IsShared && item.Score >= 70 && !item.BootStart &&
                               !(item.Kind == LeftoverKind.Folder && SafePaths.IsShallow(item.Location));
            item.IsChecked = item.Recommended;
            item.SizeBytes = Sizes.Of(item);
        }
        return items.Values
            .OrderBy(i => i.IsShared).ThenByDescending(i => i.Recommended).ThenBy(i => i.Kind)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string[] SubKeysOf(string fullPath)
    {
        var (hive, sub) = RegistryPath.Parse(fullPath);
        return RegistryPath.SubKeys(hive, sub);
    }

    /// <summary>Microsoft Store (AppX/MSIX) registrations – managed by Windows, never touched.</summary>
    private static bool IsStoreAppKey(string key) =>
        key.Contains(@"\AppX", StringComparison.OrdinalIgnoreCase) ||
        key.Contains(@"\PackagedCom", StringComparison.OrdinalIgnoreCase) ||
        key.Contains(@"\ActivatableClasses", StringComparison.OrdinalIgnoreCase) ||
        key.Contains(@"\Extensions\ContractId", StringComparison.OrdinalIgnoreCase) ||
        key.Contains(@"\AppModel", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(key.Split('\\').Last(), "_[a-z0-9]{13}$");
}
