using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>Reads installed programs from the Windows "Uninstall" registry keys.</summary>
public static partial class ProgramInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, string DisplayPrefix)[] Sources =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    private static readonly HashSet<string> NeutralUninstallers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe", "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
    };

    /// <summary>
    /// The installed programs plus programs removed recently (by Purgely or anything else) whose leftovers were not
    /// checked yet. Also updates the PC Timeline, which is what notices removals.
    /// </summary>
    public static List<InstalledProgram> LoadWithRecentlyRemoved()
    {
        var programs = Load();
        try { Timeline.Sync(programs); } catch (Exception) { /* the timeline is optional */ }
        var names = new HashSet<string>(programs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        programs.AddRange(Timeline.RecentlyRemoved().Where(r => !names.Contains(r.Name)));
        // Microsoft Store apps come last and stay out of the Timeline sync (they update themselves constantly).
        try { programs.AddRange(StoreApps.List().Where(a => !names.Contains(a.Name))); } catch (Exception) { }
        // Games of launchers that don't register them with Windows (Epic Games).
        try { programs.AddRange(LauncherGames.List().Where(a => !names.Contains(a.Name))); } catch (Exception) { }
        // Portable programs (folders with an .exe that Windows doesn't list) – offered, never pre-selected.
        try { programs.AddRange(PortableApps.Find(programs).Where(a => !names.Contains(a.Name))); } catch (Exception) { }
        return programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<InstalledProgram> Load()
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, prefix) in Sources)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                InstalledProgram? program;
                try
                {
                    using var key = uninstall.OpenSubKey(subName);
                    program = key is null ? null : Read(key, subName, $@"{prefix}\{subName}");
                }
                catch (Exception)
                {
                    continue; // unreadable key – skip it
                }

                if (program is null || !seen.Add($"{program.Name}|{program.Version}")) continue;
                program.OrphanReason = DetectOrphan(program);
                result.Add(program);
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static InstalledProgram? Read(RegistryKey key, string subName, string fullPath)
    {
        var name = Str(key, "DisplayName");
        if (name.Length == 0) return null;
        if (Int(key, "SystemComponent") == 1) return null;            // hidden system parts
        if (Str(key, "ParentKeyName").Length > 0) return null;        // updates of other programs
        var releaseType = Str(key, "ReleaseType");
        if (releaseType is "Update" or "Hotfix" or "Security Update") return null;

        var isMsi = Int(key, "WindowsInstaller") == 1;
        var productCode = GuidRegex().IsMatch(subName) ? subName : null;

        return new InstalledProgram
        {
            Name = name,
            Publisher = Str(key, "Publisher"),
            Version = Str(key, "DisplayVersion"),
            SizeKb = Int(key, "EstimatedSize") is int kb and > 0 ? kb : null,
            InstallDate = ParseDate(Str(key, "InstallDate")),
            RegisteredAt = RegistryTimestamp.LastWrite(key),
            // Inno Setup (used by many game repacks) keeps the folder in "Inno Setup: App Path".
            InstallLocation = (Str(key, "InstallLocation").Length > 0 ? Str(key, "InstallLocation") : Str(key, "Inno Setup: App Path")).Trim('"', ' '),
            UninstallString = Str(key, "UninstallString"),
            QuietUninstallString = Str(key, "QuietUninstallString"),
            DisplayIcon = Str(key, "DisplayIcon"),
            RegistryKey = fullPath,
            IsMsi = isMsi,
            MsiProductCode = isMsi ? productCode : null,
        };
    }

    /// <summary>Detects programs whose files are gone but whose registration remains.</summary>
    private static string DetectOrphan(InstalledProgram p)
    {
        var exe = UninstallCommand.ExtractExecutable(p.UninstallString);
        if (exe is not null && Path.IsPathRooted(exe)
            && !NeutralUninstallers.Contains(Path.GetFileName(exe))
            && !File.Exists(exe))
            return "Removed (uninstaller missing)";

        if (p.InstallLocation.Length > 0 && Path.IsPathRooted(p.InstallLocation)
            && !Directory.Exists(p.InstallLocation))
            return "Removed (install folder missing)";

        return "";
    }

    private static string Str(RegistryKey key, string name) =>
        key.GetValue(name) is { } v ? Environment.ExpandEnvironmentVariables(v.ToString() ?? "").Trim() : "";

    private static int? Int(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int i => i,
        string s when int.TryParse(s, out var i) => i,
        _ => null,
    };

    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex(@"^\{[0-9A-Fa-f\-]{36}\}$")]
    private static partial Regex GuidRegex();
}
