using System.IO;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// Leftover registrations (0.9): right-click menu entries, file types, program paths for the Run box, firewall rules
/// and PATH entries. Each one points at a program file or folder; it is only offered when that target is in a
/// folder that clearly belongs to this program, or when its own name matches the program and the target is gone.
/// Nothing that points into Windows is ever offered.
/// </summary>
public sealed partial class LeftoverScanner
{
    private const int LinkedPoints = 75;   // points into a leftover folder of this program
    private const int NamePoints = 50;     // name matches the program (offered, not ticked on its own)
    private const int MissingPoints = 15;  // the file or folder it points at no longer exists
    private const int NotTickedPoints = 60; // offered, but below the 70 needed to be ticked

    /// <summary>Verbs Windows itself uses – a program that took one over is offered, never ticked.</summary>
    private static readonly HashSet<string> WindowsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewtab", "edit", "print", "printto", "explore", "find", "runas", "runasuser",
        "cmd", "powershell", "play", "preview", "properties", "pintohome", "pintostartscreen", "openas", "share",
    };

    private static readonly string[] ShellClasses =
        { "*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground" };

    private static readonly (RegistryHive Hive, string Sub, string Display)[] ClassRoots =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Classes", @"HKLM\SOFTWARE\Classes"),
        (RegistryHive.CurrentUser, @"Software\Classes", @"HKCU\Software\Classes"),
    };

    private void ScanRegistrations(List<string> folders)
    {
        foreach (var (hive, sub, display) in ClassRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var classes = baseKey.OpenSubKey(sub);
                if (classes is null) continue;
                ScanContextMenus(classes, display, folders);
                ScanFileTypes(classes, display, folders);
            }
            catch (Exception) { }
        }
        Try(() => ScanAppPaths(folders));
        Try(() => ScanFirewall(folders));
        Try(() => ScanPathVariable(folders));
    }

    private static void Try(Action action)
    {
        try { action(); } catch (Exception) { }
    }

    /// <summary>How strongly a registration whose target is <paramref name="target"/> belongs to this program.</summary>
    private (int Points, string Why)? Judge(string target, string name, List<string> folders, bool targetIsFolder = false)
    {
        if (target.Length < 4 || !Path.IsPathRooted(target)) return null;
        if (PathUtil.SameOrUnder(target, PathUtil.WindowsDir)) return null; // Windows' own – never
        var missing = targetIsFolder ? !Directory.Exists(target) : !File.Exists(target);
        var folder = folders.FirstOrDefault(f => PathUtil.SameOrUnder(target, f));
        if (folder is not null)
            return (LinkedPoints + (missing ? MissingPoints : 0),
                    $"Points into a leftover folder ({Path.GetFileName(folder.TrimEnd('\\'))})" + (missing ? ", and the target is gone" : ""));
        if (!missing) return null; // a working registration of something else
        var targetName = targetIsFolder ? Path.GetFileName(target.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(target);
        var parentName = Path.GetFileName(Path.GetDirectoryName(target.TrimEnd('\\')) ?? "");
        if (MatchStrict(name) || MatchStrict(targetName) || MatchStrict(parentName))
            return (NamePoints + MissingPoints, "Name matches the program, and what it points at is gone");
        return null;
    }

    private void Offer(LeftoverKind kind, string location, (int Points, string Why) judged, string detail, string target)
    {
        if (kind is not (LeftoverKind.FirewallRule or LeftoverKind.PathEntry) && Cleaner.IsCriticalKey(location)) return;
        var item = Get(kind, location);
        item.AddEvidence(judged.Points, judged.Why);
        if (item.Detail.Length == 0) item.Detail = detail;
        if (item.TargetPath.Length == 0) item.TargetPath = target;
    }

    // ------------------------------------------------------------------ right-click menu
    private void ScanContextMenus(RegistryKey classes, string display, List<string> folders)
    {
        foreach (var cls in ShellClasses)
        {
            // Verbs: <class>\shell\<verb>\command = "C:\...\program.exe" "%1"
            using (var shell = TryOpen(classes, $@"{cls}\shell"))
            {
                if (shell is not null)
                    foreach (var verb in shell.GetSubKeyNames())
                    {
                        using var command = TryOpen(shell, $@"{verb}\command");
                        var exe = PathUtil.ExtractExecutable(command?.GetValue("")?.ToString() ?? "");
                        using var verbKey = TryOpen(shell, verb);
                        var label = verbKey?.GetValue("MUIVerb")?.ToString() ?? verbKey?.GetValue("")?.ToString() ?? verb;
                        if (label.StartsWith('@')) label = verb;
                        if (Judge(exe, verb, folders) is not { } j) continue;
                        if (WindowsVerbs.Contains(verb))
                            j = (Math.Min(j.Points, NotTickedPoints), j.Why + " – a standard Windows menu entry the program took over; check before removing");
                        Offer(LeftoverKind.ContextMenu, $@"{display}\{cls}\shell\{verb}", j, $"\"{label.Replace("&", "")}\" in the right-click menu", exe);
                    }
            }

            // Shell extensions: <class>\shellex\ContextMenuHandlers\<name> = {CLSID} → CLSID\{..}\InprocServer32 = the DLL
            using var handlers = TryOpen(classes, $@"{cls}\shellex\ContextMenuHandlers");
            if (handlers is null) continue;
            foreach (var handler in handlers.GetSubKeyNames())
            {
                using var h = TryOpen(handlers, handler);
                var clsid = h?.GetValue("")?.ToString() ?? "";
                if (!clsid.StartsWith('{') && handler.StartsWith('{')) clsid = handler;
                if (!System.Text.RegularExpressions.Regex.IsMatch(clsid, @"^\{[0-9A-Fa-f\-]{36}\}$")) continue;
                var sameHive = ServerIn(display, clsid);
                var dll = sameHive.Length > 0 ? sameHive : ServerOf(clsid);
                if (Judge(dll, handler, folders) is not { } j) continue;
                Offer(LeftoverKind.ContextMenu, $@"{display}\{cls}\shellex\ContextMenuHandlers\{handler}", j,
                      $"Right-click menu extension \"{handler}\"", dll);
                // The extension's own registration – only when it is in the same place and points at the same leftover.
                if (sameHive.Length > 0 && Judge(sameHive, handler, folders) is { } own)
                    Offer(LeftoverKind.ContextMenu, $@"{display}\CLSID\{clsid}", own, $"Registration of the \"{handler}\" extension", sameHive);
            }
        }
    }

    /// <summary>The DLL a COM class runs from, read from one classes root ("HKLM\SOFTWARE\Classes" or "HKCU\Software\Classes").</summary>
    private static string ServerIn(string classesRoot, string clsid)
    {
        try
        {
            using var key = RegistryPath.Open($@"{classesRoot}\CLSID\{clsid}\InprocServer32");
            var value = key?.GetValue("")?.ToString();
            return string.IsNullOrWhiteSpace(value) ? "" : PathUtil.ExtractExecutable(value);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>The DLL a COM class runs from (64-bit registration first, then 32-bit).</summary>
    private static string ServerOf(string clsid)
    {
        foreach (var path in new[] { $@"HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32", $@"HKCU\Software\Classes\CLSID\{clsid}\InprocServer32",
                                     $@"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\{clsid}\InprocServer32" })
        {
            try
            {
                using var key = RegistryPath.Open(path);
                var value = key?.GetValue("")?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return PathUtil.ExtractExecutable(value);
            }
            catch (Exception) { }
        }
        return "";
    }

    // ------------------------------------------------------------------ file types
    /// <summary>
    /// File types (ProgIDs) that open with the program, found through the extensions that use them
    /// (.mp4 → "VLC.mp4" → shell\open\command), plus "Open with" entries under Applications\program.exe.
    /// The extension keys themselves (.mp4) and your own default-app choices are never touched.
    /// </summary>
    private void ScanFileTypes(RegistryKey classes, string display, List<string> folders)
    {
        var progIds = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in classes.GetSubKeyNames().Where(n => n.Length > 1 && n[0] == '.'))
        {
            using var key = TryOpen(classes, ext);
            if (key is null) continue;
            void Use(string? id)
            {
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith('.')) return;
                if (!progIds.TryGetValue(id, out var set)) progIds[id] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(ext);
            }
            Use(key.GetValue("")?.ToString());
            using var openWith = TryOpen(key, "OpenWithProgids");
            if (openWith is not null) foreach (var id in openWith.GetValueNames()) Use(id);
        }

        foreach (var (id, exts) in progIds)
        {
            if (id.Contains('\\') || Keywords.Protected.Contains(Keywords.Norm(id))) continue;
            using var command = TryOpen(classes, $@"{id}\shell\open\command");
            var exe = PathUtil.ExtractExecutable(command?.GetValue("")?.ToString() ?? "");
            if (Judge(exe, id, folders) is not { } j) continue;
            var list = string.Join(", ", exts.Take(4)) + (exts.Count > 4 ? $" and {exts.Count - 4} more" : "");
            if (IsOwnProgId(id))
            {
                // The file type is the program's own ("VLC.mp4") – the whole registration goes.
                Offer(LeftoverKind.FileType, $@"{display}\{id}", j, $"Opens {list}", exe);
            }
            else
            {
                // Someone else's file type (often Windows' own: txtfile, jpegfile) whose "open" the program took over:
                // only that command is offered – never ticked – and the file type itself stays.
                Offer(LeftoverKind.FileType, $@"{display}\{id}\shell\open", (Math.Min(j.Points, NotTickedPoints),
                      j.Why + " – the file type belongs to Windows or another program, so only its \"open\" command is offered"),
                      $"\"Open\" command of {list}", exe);
            }
        }

        using var apps = TryOpen(classes, "Applications");
        if (apps is null) return;
        foreach (var app in apps.GetSubKeyNames())
        {
            using var command = TryOpen(apps, $@"{app}\shell\open\command");
            var exe = PathUtil.ExtractExecutable(command?.GetValue("")?.ToString() ?? "");
            if (Judge(exe, Path.GetFileNameWithoutExtension(app), folders) is { } j)
                Offer(LeftoverKind.FileType, $@"{display}\Applications\{app}", j, $"\"Open with\" entry for {app}", exe);
        }
    }

    /// <summary>"VLC.mp4", "Notepad++_file": the ProgID's own name (or its first part) is the program's.</summary>
    private bool IsOwnProgId(string id)
    {
        if (MatchStrict(id)) return true;
        var first = Keywords.Norm(id.Split('.', '_')[0]);
        return first.Length >= 3 && !Keywords.Protected.Contains(first) && _t.Strong.Contains(first);
    }

    // ------------------------------------------------------------------ App Paths (Win+R)
    private static readonly (RegistryHive Hive, string Sub, string Display)[] AppPathRoots =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
        (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths"),
    };

    private void ScanAppPaths(List<string> folders)
    {
        foreach (var (hive, sub, display) in AppPathRoots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(sub);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = TryOpen(root, name);
                var exe = PathUtil.ExtractExecutable(key?.GetValue("")?.ToString() ?? "");
                if (Judge(exe, Path.GetFileNameWithoutExtension(name), folders) is { } j)
                    Offer(LeftoverKind.AppPath, $@"{display}\{name}", j, $"Typing \"{Path.GetFileNameWithoutExtension(name)}\" in Win+R starts it", exe);
            }
        }
    }

    // ------------------------------------------------------------------ firewall
    private const string FirewallRulesKey = @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>Firewall rules for a program file (App=...). Windows keeps them after the program is gone.</summary>
    private void ScanFirewall(List<string> folders)
    {
        using var key = RegistryPath.Open(FirewallRulesKey);
        if (key is null) return;
        foreach (var id in key.GetValueNames())
        {
            if (key.GetValue(id) is not string rule) continue;
            string Field(string name)
            {
                foreach (var part in rule.Split('|'))
                    if (part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return part[(name.Length + 1)..];
                return "";
            }
            var app = Field("App");
            if (app.Length == 0) continue;
            var exe = Environment.ExpandEnvironmentVariables(app);
            var ruleName = Field("Name");
            if (ruleName.StartsWith('@')) ruleName = "";
            if (Judge(exe, ruleName.Length > 0 ? ruleName : Path.GetFileNameWithoutExtension(exe), folders) is not { } j) continue;
            var item = Get(LeftoverKind.FirewallRule, $@"{FirewallRulesKey}\{id}");
            item.AddEvidence(j.Points, j.Why);
            item.RegistryKeyPath = FirewallRulesKey;
            item.ValueName = id;
            item.Detail = ruleName.Length > 0 ? ruleName : Path.GetFileName(exe);
            item.TargetPath = exe;
        }
    }

    // ------------------------------------------------------------------ PATH
    /// <summary>PATH entries that point into this program's folders (or to a gone folder named after it).</summary>
    private void ScanPathVariable(List<string> folders)
    {
        foreach (var keyPath in new[] { PathVariable.SystemKey, PathVariable.UserKey })
        {
            foreach (var raw in PathVariable.Read(keyPath))
            {
                var expandedRaw = Environment.ExpandEnvironmentVariables(raw).Trim('"');
                if (expandedRaw.Contains('%') || !System.Text.RegularExpressions.Regex.IsMatch(expandedRaw, @"^[A-Za-z]:\\.")) continue;
                string expanded;
                try { expanded = PathUtil.Normalize(expandedRaw); } catch (Exception) { continue; }
                if (_blockedFolders.Contains(expanded)) continue; // a root like Program Files itself
                if (Judge(expanded, Path.GetFileName(expanded.TrimEnd('\\')), folders, targetIsFolder: true) is not { } j) continue;
                var item = Get(LeftoverKind.PathEntry, $"{keyPath}|{raw}");
                item.AddEvidence(j.Points, j.Why);
                item.RegistryKeyPath = keyPath;
                item.ValueName = PathVariable.ValueName;
                item.Detail = raw;
                item.TargetPath = expanded;
            }
        }
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string sub)
    {
        try { return parent.OpenSubKey(sub); }
        catch (Exception) { return null; }
    }
}
