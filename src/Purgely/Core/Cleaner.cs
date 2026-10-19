using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Purgely.Core;

public sealed class CleanReport
{
    public int Removed { get; set; }
    public int ScheduledForReboot { get; set; }
    public List<string> Notes { get; } = new();
    public List<string> Failures { get; } = new();
    public string PackageFolder { get; set; } = "";
}

/// <summary>Removes leftovers. Everything is saved into an Undo package first.</summary>
public static class Cleaner
{
    /// <summary>Structural registry keys that are never deleted, whatever the evidence says.</summary>
    private static readonly HashSet<string> CriticalKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "software", "classes", "wow6432node", "policies", "microsoft", "windows", "windowsnt", "currentversion",
        "apppaths", "protocols", "handler", "filter", "clsid", "interface", "typelib", "appid", "shellex",
        "contextmenuhandlers", "dragdrophandlers", "propertysheethandlers", "iconhandler", "shell",
        "run", "runonce", "uninstall", "services", "installer", "explorer", "system", "currentcontrolset",
        "controlset001", "directory", "drive", "folder", "allfilesystemobjects", "background", "mimedatabase",
        "contenttype", "fileexts", "applications", "systemfileassociations", "sharedlls", "shareddlls",
    };

    public static bool IsCriticalKey(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        if (parts.Length < 3) return true;
        // Microsoft Store package names (Name_8wekyb3d8bbwe) are managed by Windows.
        if (System.Text.RegularExpressions.Regex.IsMatch(parts[^1], "_[a-z0-9]{13}$")) return true;
        var last = Keywords.Norm(parts[^1]);
        return last.Length == 0 || CriticalKeyNames.Contains(last) || Keywords.Protected.Contains(last);
    }

    /// <summary>Name of the per-drive Undo folder used for files that live on another drive than Windows.</summary>
    public const string DriveUndoFolderName = "Purgely Undo";

    /// <summary>Where a file's backup goes: inside the package, or "X:\Purgely Undo\(package name)" on the file's own drive.</summary>
    public static string BackupFolderFor(string source, string package)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)) ?? "";
        var packageRoot = Path.GetPathRoot(Path.GetFullPath(package)) ?? "";
        if (sourceRoot.Length == 0 || string.Equals(sourceRoot, packageRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(package, "files");
        var folder = Path.Combine(sourceRoot, DriveUndoFolderName);
        try
        {
            // Only a folder that is (or can be made) administrators-only – never a link, never one a user prepared.
            if (!DataFolders.PrepareDriveUndoFolder(folder)) return Path.Combine(package, "files");
            new DirectoryInfo(folder).Attributes |= FileAttributes.Hidden;
            File.WriteAllText(Path.Combine(folder, "README.txt"),
                "Backups made by Purgely (Undo center). Restore or discard them from Purgely – don't delete by hand.");
        }
        catch (Exception) { return Path.Combine(package, "files"); }
        return Path.Combine(folder, Path.GetFileName(package));
    }

    public static string UndoRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Purgely", "Undo");

    public static CleanReport Clean(string title, IReadOnlyList<LeftoverItem> items,
                                    IProgress<(int Done, int Total, string Current)>? progress = null)
    {
        using var busy = Activity.Begin();
        var processed = 0;
        void Step(LeftoverItem item) => progress?.Report((++processed, items.Count, item.DisplayLocation));
        var safeTitle = string.Concat(title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
        if (safeTitle.Length > 40) safeTitle = safeTitle[..40];
        var package = Path.Combine(UndoRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeTitle}");
        Directory.CreateDirectory(package);

        var manifest = new UndoManifest { Title = title, Created = DateTime.Now };
        var report = new CleanReport { PackageFolder = package };
        var n = 0;
        string NextFile(string suffix) => Path.Combine(package, $"{++n:000}-{suffix}");
        void Save() => UndoStore.Save(package, manifest);
        void Done(LeftoverItem item, UndoAction action)
        {
            manifest.Actions.Add(action);
            item.Removed = true;
            report.Removed++;
            Save();
        }

        // 1. Services and drivers – back up first, then stop them so their files are released, then delete.
        var services = new List<(LeftoverItem Item, string Backup)>();
        foreach (var s in items.Where(i => i.Kind is LeftoverKind.Service or LeftoverKind.Driver))
        {
            var backup = NextFile("service.reg");
            if (Shell.Run("reg", $"export \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{s.Location}\" \"{backup}\" /y /reg:64").Code != 0)
            {
                Step(s);
                report.Failures.Add($"{s.DisplayLocation}: backup failed, nothing removed");
                continue;
            }
            services.Add((s, backup));
        }
        foreach (var (s, _) in services) Shell.Run("sc", $"stop \"{s.Location}\"", 20_000);
        if (services.Count > 0) Thread.Sleep(1500);
        foreach (var (s, backup) in services)
        {
            Step(s);
            var (code, output) = Shell.Run("sc", $"delete \"{s.Location}\"");
            if (code is 0 or 1072) // 1072 = marked for deletion, finished at restart
            {
                if (code == 1072) { report.ScheduledForReboot++; manifest.RestartPending = true; }
                Done(s, new UndoAction { Type = "Service", Original = s.Location, Backup = backup });
            }
            else report.Failures.Add($"{s.DisplayLocation}: {output}");
        }

        // 2. Scheduled tasks.
        foreach (var t in items.Where(i => i.Kind == LeftoverKind.ScheduledTask))
        {
            Step(t);
            var backup = NextFile("task.xml");
            try { File.Copy(Path.Combine(TaskStore.Root, t.Location.TrimStart('\\')), backup, true); }
            catch (Exception) { report.Failures.Add($"{t.Location}: backup failed, nothing removed"); continue; }

            var (code, output) = Shell.Run("schtasks", $"/Delete /TN \"{t.Location}\" /F");
            if (code == 0) Done(t, new UndoAction { Type = "Task", Original = t.Location, Backup = backup });
            else report.Failures.Add($"{t.Location}: {output}");
        }

        // 3. Startup entries (registry Run values) – the exact value (any type) is saved for Undo.
        foreach (var r in items.Where(i => i.Kind == LeftoverKind.StartupEntry))
        {
            Step(r);
            try
            {
                using var key = RegistryPath.Open(r.RegistryKeyPath, writable: true);
                if (key is null || !key.GetValueNames().Contains(r.ValueName, StringComparer.OrdinalIgnoreCase))
                { r.Removed = true; continue; }
                var saved = RegValue.Read(key, r.ValueName);
                if (saved is null || !saved.CanRoundTrip)
                {
                    report.Failures.Add($"{r.DisplayLocation}: value can't be backed up exactly, nothing removed");
                    continue;
                }
                key.DeleteValue(r.ValueName, false);
                Done(r, new UndoAction
                {
                    Type = "Value", Original = r.RegistryKeyPath, Extra = r.ValueName,
                    Backup = System.Text.Json.JsonSerializer.Serialize(saved),
                });
            }
            catch (Exception ex) { report.Failures.Add($"{r.DisplayLocation}: {ex.Message}"); }
        }

        var firewallAfterRestart = false;
        // 3b. Registry values (Trial Mode): remove added values, put back changed or deleted ones.
        // Firewall rules are single values too: removed (OldValue null), the exact value saved for Undo.
        foreach (var v in items.Where(i => i.Kind is LeftoverKind.RegistryValue or LeftoverKind.FirewallRule))
        {
            Step(v);
            try
            {
                using var key = RegistryPath.Create(v.RegistryKeyPath);
                var exists = key.GetValueNames().Contains(v.ValueName, StringComparer.OrdinalIgnoreCase);
                if (!exists && v.Kind == LeftoverKind.FirewallRule) { v.Removed = true; continue; } // already gone
                var current = exists ? RegValue.Read(key, v.ValueName) : null;
                if (exists && (current is null || !current.CanRoundTrip))
                {
                    report.Failures.Add($"{v.DisplayLocation}: current value can't be backed up exactly, nothing changed");
                    continue;
                }
                if (v.OldValue is { CanRoundTrip: false })
                {
                    report.Failures.Add($"{v.DisplayLocation}: unsupported value type, nothing changed");
                    continue;
                }
                if (v.Kind == LeftoverKind.FirewallRule)
                {
                    // Through the firewall itself, so the rule stops at once; the registry only if that is refused.
                    if (!RemoveFirewallRule(v.ValueName)) { key.DeleteValue(v.ValueName, false); firewallAfterRestart = true; }
                }
                else if (v.OldValue is null) key.DeleteValue(v.ValueName, false);
                else v.OldValue.WriteTo(key, v.ValueName);
                Done(v, new UndoAction
                {
                    Type = v.Kind == LeftoverKind.FirewallRule ? "FirewallRule" : "Value", Original = v.RegistryKeyPath, Extra = v.ValueName,
                    Backup = current is null ? "" : System.Text.Json.JsonSerializer.Serialize(current),
                });
            }
            catch (Exception ex) { report.Failures.Add($"{v.DisplayLocation}: {ex.Message}"); }
        }

        // 3c. PATH entries – only the one entry is taken out of the value; Undo puts it back.
        foreach (var p in items.Where(i => i.Kind == LeftoverKind.PathEntry))
        {
            Step(p);
            try
            {
                if (!PathVariable.RemoveEntry(p.RegistryKeyPath, p.Detail, out var wasThere, out var position))
                {
                    report.Failures.Add($"{p.DisplayLocation}: PATH has an unusual type, nothing changed");
                    continue;
                }
                if (!wasThere) { p.Removed = true; continue; }
                Done(p, new UndoAction { Type = "PathEntry", Original = p.RegistryKeyPath, Extra = p.Detail, Extra2 = PathVariable.ValueName,
                                         Backup = position.ToString() });
            }
            catch (Exception ex) { report.Failures.Add($"{p.DisplayLocation}: {ex.Message}"); }
        }
        if (firewallAfterRestart)
            report.Notes.Add("Some firewall rules are removed from Windows Firewall after the next restart.");

        // 4. Stop programs still running from the folders we are about to remove.
        var folders = items.Where(i => i.Kind == LeftoverKind.Folder && SafePaths.IsRemovableFolder(i.Location, out _))
                           .Select(i => i.Location).ToList();
        if (folders.Count > 0) StopProcessesIn(folders, report);

        // 5. Folders and shortcuts → moved into the Undo package.
        foreach (var f in items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.Shortcut or LeftoverKind.File))
        {
            Step(f);
            var isFolder = f.Kind == LeftoverKind.Folder;
            if (isFolder ? !Directory.Exists(f.Location) : !File.Exists(f.Location)) { f.Removed = true; continue; }

            // Final guard: whatever the scanner said, never touch Windows, user folders or links.
            if (isFolder ? !SafePaths.IsRemovableFolder(f.Location, out var why) : !SafePaths.IsRemovableFile(f.Location, out why))
            {
                report.Failures.Add($"{f.Location}: protected ({why}) – Purgely never removes it");
                continue;
            }

            // Backups stay on the same drive as the original: a 40 GB game on D:\ is renamed in a second
            // instead of being copied to C:\ (which might not even have the space).
            var destination = Path.Combine(BackupFolderFor(f.Location, package), $"{++n:000}", Path.GetFileName(f.Location));
            var result = FileMover.MoveToBackup(f.Location, destination, isFolder);
            if (!result.BackedUp)
            {
                report.Failures.Add($"{f.Location}: {result.Error}");
                continue;
            }
            if (result.ScheduledForReboot > 0)
            {
                report.ScheduledForReboot += result.ScheduledForReboot;
                report.Notes.Add($"{f.Location}: {result.ScheduledForReboot} locked file(s) will be removed when you restart");
            }
            if (result.NotBackedUp > 0)
                report.Notes.Add($"{f.Location}: {result.NotBackedUp} file(s) could not be backed up, so they were left in place");
            Done(f, new UndoAction { Type = isFolder ? "Folder" : "File", Original = f.Location, Backup = destination });
        }

        // 6. Registry keys – exported first, then deleted.
        foreach (var k in items.Where(i => i.IsRegistryKeyKind))
        {
            Step(k);
            if (!RegistryPath.Exists(k.Location)) { k.Removed = true; continue; }
            if (IsCriticalKey(k.Location))
            {
                report.Failures.Add($"{k.Location}: protected Windows key – Purgely never deletes it");
                continue;
            }
            var backup = NextFile("key.reg");
            if (Shell.Run("reg", $"export \"{k.Location}\" \"{backup}\" /y /reg:64").Code != 0)
            {
                report.Failures.Add($"{k.Location}: backup failed, nothing removed");
                continue;
            }
            var (code, output) = Shell.Run("reg", $"delete \"{k.Location}\" /f /reg:64");
            var action = new UndoAction { Type = "RegKey", Original = k.Location, Backup = backup };
            if (code == 0) Done(k, action);
            else
            {
                // Even a failed delete may have removed part of the key – keep the backup restorable.
                manifest.Actions.Add(action);
                Save();
                report.Failures.Add($"{k.Location}: {output} (backup kept – the Undo center can restore it)");
            }
        }

        Save();
        if (manifest.Actions.Count == 0) FileMover.DeleteBestEffort(package, true);
        else Timeline.Log(TimelineEventType.Cleaned, title, $"{report.Removed} leftover item(s) removed – can be undone");
        return report;
    }

    /// <summary>Removes one firewall rule by its ID (the registry value name) through Windows Firewall.</summary>
    private static bool RemoveFirewallRule(string id)
    {
        if (id.Length == 0 || id.Any(c => c is '"' or '\0' or '\r' or '\n')) return false;
        var quoted = "'" + id.Replace("'", "''") + "'";
        var (code, _) = Shell.Run("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-NetFirewallRule -Name {quoted} -ErrorAction Stop\"", 60_000);
        return code == 0;
    }

    private static void StopProcessesIn(List<string> folders, CleanReport report)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                var file = process.MainModule?.FileName;
                if (file is null || !folders.Any(f => PathUtil.IsUnder(file, f))) continue;
                process.Kill(entireProcessTree: false); // only the leftover program itself, not what it started
                process.WaitForExit(3000);
                report.Notes.Add($"Stopped running program: {process.ProcessName}");
            }
            catch (Exception) { /* no access to system processes – ignore */ }
            finally { process.Dispose(); }
        }
    }
}
