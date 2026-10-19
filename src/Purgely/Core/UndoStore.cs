using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core;

public sealed class UndoAction
{
    /// <summary>Folder, File, RegKey, RunValue, Value, PathEntry, Task, Service, Driver, Appx.</summary>
    public string Type { get; set; } = "";
    public string Original { get; set; } = "";
    public string Backup { get; set; } = "";
    public string Extra { get; set; } = "";
    public string Extra2 { get; set; } = "";
}

public sealed class UndoManifest
{
    public string Title { get; set; } = "";
    public DateTime Created { get; set; }
    public bool Restored { get; set; }
    /// <summary>A service was only marked for deletion – it is finished at the next restart.</summary>
    public bool RestartPending { get; set; }
    /// <summary>Indexes of actions that could not be put back last time.</summary>
    public List<int> NotRestored { get; set; } = new();
    public List<UndoAction> Actions { get; set; } = new();
}

/// <summary>A saved Undo package as shown in the Undo center.</summary>
public sealed class UndoEntry
{
    public required string Folder { get; init; }
    public required UndoManifest Manifest { get; init; }
    public string Title => Manifest.Title;
    public string CreatedText => Manifest.Created.ToString("yyyy-MM-dd HH:mm");
    public int Count => Manifest.Actions.Count;
    public string Status => Manifest.Restored ? "Restored"
        : Manifest.NotRestored.Count > 0 && Manifest.NotRestored.All(i => i < Count && Manifest.Actions[i].Type == "Appx")
            ? "Store page opened – install it there"
        : Manifest.NotRestored.Count > 0 ? $"Partly restored ({Manifest.NotRestored.Count} left)"
        : "Can be restored";
}

/// <summary>Stores Undo packages and restores them.</summary>
public static class UndoStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Written to a temporary file first, so a crash never leaves a half-written manifest.</summary>
    public static void Save(string folder, UndoManifest manifest)
    {
        var target = Path.Combine(folder, "manifest.json");
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temp, target, overwrite: true);
        DataFolders.OwnByAdministrators(target);
        DataFolders.OwnByAdministrators(folder);
    }

    /// <summary>Creates an empty Undo package folder (used by features other than the Cleaner, e.g. driver updates).</summary>
    public static string CreatePackage(string title)
    {
        var safe = string.Concat(title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
        if (safe.Length > 40) safe = safe[..40];
        var folder = Path.Combine(Cleaner.UndoRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Saves a manifest with the given actions (used by features that change settings step by step).</summary>
    public static void SaveActions(string package, string title, List<UndoAction> actions)
    {
        var manifest = new UndoManifest { Title = title, Created = DateTime.Now };
        manifest.Actions.AddRange(actions);
        Save(package, manifest);
    }

    /// <summary>Records a driver installed by Purgely: Undo removes the new package(s) so Windows falls back.</summary>
    public static void AddDriverAction(string package, string title, string device, string previousDriverFolder, List<string> publishedInfs)
    {
        var manifest = new UndoManifest { Title = title, Created = DateTime.Now };
        manifest.Actions.Add(new UndoAction
        {
            Type = "Driver", Original = device, Backup = previousDriverFolder, Extra = string.Join(";", publishedInfs),
        });
        Save(package, manifest);
    }

    public static List<UndoEntry> List()
    {
        var result = new List<UndoEntry>();
        if (!Directory.Exists(Cleaner.UndoRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(Cleaner.UndoRoot))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<UndoManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
                if (manifest is not null) result.Add(new UndoEntry { Folder = dir, Manifest = manifest });
            }
            catch (Exception) { }
        }
        return result.OrderByDescending(e => e.Manifest.Created).ToList();
    }

    /// <summary>Puts everything in the package back. Returns notes for the user.</summary>
    public static List<string> Restore(UndoEntry entry)
    {
        using var busy = Activity.Begin();
        var notes = new List<string>();
        var restartNeeded = false;

        // A package tells Purgely (as administrator) what to put back. Only packages that nobody but administrators could
        // have written are restored – a planted "package" must never run with Purgely's rights.
        var manifestFile = Path.Combine(entry.Folder, "manifest.json");
        // The package folder: owner and permissions; manifest.json: permissions (its owner may be the admin account itself).
        bool Trusted() => DataFolders.IsAdminOnly(entry.Folder) && DataFolders.IsAdminOnly(manifestFile, checkOwner: false);
        if (!Trusted()) DataFolders.Protect(); // packages made before 0.9 get the new protection first
        if (!Trusted())
        {
            notes.Add("This package was not written by Purgely with administrator rights (someone else can change it), " +
                      "so it is not restored. If you made it with Purgely, start Purgely as administrator and try again.");
            return notes;
        }
        var failed = new List<int>();

        if (entry.Manifest.RestartPending && entry.Manifest.Actions.Any(a => a.Type == "Service"))
            notes.Add("Note: a removed service was waiting for a restart. If it is missing after restarting, " +
                      "restore this package again.");

        var actions = entry.Manifest.Actions;
        var todo = entry.Manifest.NotRestored.Count > 0 ? entry.Manifest.NotRestored : Enumerable.Range(0, actions.Count).ToList();
        foreach (var index in todo.OrderByDescending(i => i))
        {
            var a = actions[index];
            bool ok = true;
            try
            {
                switch (a.Type)
                {
                    case "Folder":
                    case "File":
                        if (!BackupIsTrusted(a.Backup, entry.Folder))
                        {
                            notes.Add($"Not restored: the backup of {a.Original} is in a folder other users can change.");
                            ok = false;
                            break;
                        }
                        if (!Directory.Exists(a.Backup) && !File.Exists(a.Backup))
                        {
                            notes.Add($"Backup is missing: {a.Original}");
                            ok = false;
                            break;
                        }
                        if (a.Type == "File" && File.Exists(a.Original))
                        {
                            notes.Add($"Skipped, already exists: {a.Original}");
                            break;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(a.Original)!);
                        var kept = FileMover.MoveBack(a.Backup, a.Original, a.Type == "Folder"); // merges into an existing folder
                        if (kept > 0)
                            notes.Add($"{a.Original}: {kept} file(s) already existed in a newer version and were not overwritten " +
                                      "(the old copies stay in this package until you discard it)");
                        break;

                    case "RegKey":
                        if (Shell.Run("reg", $"import \"{a.Backup}\" /reg:64").Code != 0)
                        {
                            notes.Add($"Could not restore registry key {a.Original}");
                            ok = false;
                        }
                        break;

                    case "RunValue": // packages made by older versions
                        using (var key = CreateKey(a.Original))
                            key.SetValue(a.Extra, a.Backup, Enum.Parse<RegistryValueKind>(a.Extra2));
                        break;

                    case "Value":
                        using (var key = RegistryPath.Create(a.Original))
                        {
                            if (a.Backup.Length == 0) key.DeleteValue(a.Extra, false);
                            else JsonSerializer.Deserialize<RegValue>(a.Backup)?.WriteTo(key, a.Extra);
                        }
                        Bloatware.ForgetRestored(new[] { a.Original + "|" + a.Extra }, Array.Empty<string>());
                        break;

                    case "FirewallRule":
                        // Written back where Windows keeps firewall rules; Windows Firewall reads it again at the next restart.
                        using (var key = RegistryPath.Create(a.Original))
                            JsonSerializer.Deserialize<RegValue>(a.Backup)?.WriteTo(key, a.Extra);
                        restartNeeded = true;
                        break;

                    case "PathEntry":
                        PathVariable.AddEntry(a.Original, a.Extra, int.TryParse(a.Backup, out var at) ? at : -1);
                        break;

                    case "Task":
                        if (Shell.Run("schtasks", $"/Create /XML \"{a.Backup}\" /TN \"{a.Original}\" /F").Code != 0)
                        {
                            notes.Add($"Could not restore scheduled task {a.Original}");
                            ok = false;
                        }
                        break;

                    case "Appx":
                        // A removed Store app cannot be copied back – open its Store page so the user can reinstall it.
                        if (System.Text.RegularExpressions.Regex.IsMatch(a.Original, @"^[\w.\-]+_[a-z0-9]{13}$"))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(a.Original)}") { UseShellExecute = true });
                            notes.Add($"{a.Extra}: the Microsoft Store opened – click Get/Install to put the app back. " +
                                      "This entry stays here until you discard it, in case you want to open the Store page again.");
                            ok = false; // not really restored until the user installs it
                            Bloatware.ForgetRestored(Array.Empty<string>(), new[] { a.Original });
                        }
                        else notes.Add($"{a.Extra}: search for it in the Microsoft Store to reinstall it.");
                        break;

                    case "Driver":
                        // Remove the driver package(s) Purgely installed; Windows then uses the previous driver again.
                        if (a.Extra.Length == 0)
                        {
                            notes.Add($"{a.Original}: no new driver package was recorded – if the device misbehaves, " +
                                      "use Device Manager → the device → Driver → Roll Back Driver.");
                            ok = false;
                        }
                        foreach (var inf in a.Extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var (code, output) = Shell.Run("pnputil.exe", $"/delete-driver {inf} /uninstall", 300_000);
                            if (code is not (0 or 3010))
                            {
                                notes.Add($"Could not remove driver {inf} ({output.Split('\n').LastOrDefault()?.Trim()}). " +
                                          "Use Device Manager → the device → Driver → Roll Back Driver.");
                                ok = false;
                            }
                            else restartNeeded = true;
                        }
                        // The exported previous driver is put back into the driver store, in case it was removed.
                        if (Directory.Exists(a.Backup) && Directory.EnumerateFiles(a.Backup, "*.inf", SearchOption.AllDirectories).Any())
                        {
                            var (reAdd, _) = Shell.Run("pnputil.exe", $"/add-driver \"{Path.Combine(a.Backup, "*.inf")}\" /subdirs /install", 600_000);
                            if (reAdd is not (0 or 3010 or 259))
                                notes.Add($"{a.Original}: the saved previous driver could not be added back (code {reAdd}) – Windows uses its best remaining driver.");
                        }
                        break;

                    case "Service":
                        if (Shell.Run("reg", $"import \"{a.Backup}\" /reg:64").Code != 0)
                        {
                            notes.Add($"Could not restore service {a.Original}");
                            ok = false;
                        }
                        else restartNeeded = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Failed: {a.Original} – {ex.Message}");
                ok = false;
            }
            if (!ok) failed.Add(index);
        }

        if (restartNeeded) notes.Add("Restored services and firewall rules become active after you restart the PC.");
        entry.Manifest.NotRestored = failed;
        entry.Manifest.Restored = failed.Count == 0;
        Save(entry.Folder, entry.Manifest);
        Timeline.Log(TimelineEventType.Restored, entry.Title, failed.Count == 0
            ? $"{entry.Count} item(s) put back from the Undo center"
            : $"{entry.Count - failed.Count} of {entry.Count} item(s) put back – {failed.Count} can be tried again");
        return notes;
    }

    /// <summary>A backup inside the package, or inside an administrators-only "X:\Purgely Undo" folder.</summary>
    private static bool BackupIsTrusted(string backup, string package)
    {
        if (PathUtil.SameOrUnder(backup, package)) return true;
        var root = Path.GetPathRoot(backup) ?? "";
        var driveFolder = Path.Combine(root, Cleaner.DriveUndoFolderName);
        return root.Length > 0 && PathUtil.IsUnder(backup, driveFolder) && !FileMover.IsReparsePoint(driveFolder) &&
               DataFolders.IsAdminOnly(driveFolder);
    }

    /// <summary>Permanently deletes a package to free disk space.</summary>
    public static void Discard(UndoEntry entry)
    {
        // Backups kept on other drives ("X:\Purgely Undo\<package>") go too – only folders with exactly that shape.
        var packageName = Path.GetFileName(entry.Folder);
        var others = entry.Manifest.Actions
            .Where(a => a.Type is "Folder" or "File" && a.Backup.Length > 3)
            .Select(a => Path.Combine(Path.GetPathRoot(a.Backup) ?? "", Cleaner.DriveUndoFolderName, packageName))
            .Where(d => !PathUtil.SameOrUnder(d, entry.Folder) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var dir in others) FileMover.DeleteBestEffort(dir, true);
        FileMover.DeleteBestEffort(entry.Folder, true);
    }

    private static RegistryKey CreateKey(string path)
    {
        var (hive, sub) = RegistryPath.Parse(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return baseKey.CreateSubKey(sub, true);
    }
}
