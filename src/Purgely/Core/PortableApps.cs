using System.IO;

namespace Purgely.Core;

/// <summary>
/// Programs that were never installed – a folder with an .exe that Windows doesn't list (portable tools, unzipped
/// games). Looked for one level below each fixed drive and in the usual portable places. They are only offered:
/// "Uninstall" runs a leftover scan for the folder, and a folder directly on a drive is never pre-selected.
/// </summary>
public static class PortableApps
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Recovery", "PerfLogs", "$Recycle.Bin",
        "System Volume Information", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "Windows.old", "Intel", "AMD", "NVIDIA",
        "Drivers", "OneDriveTemp", "XboxGames", "WindowsApps", "WpSystem", "Purgely Undo", "MSOCache", "Config.Msi",
        "Documents and Settings", "inetpub", "ESD", "SteamLibrary", "Steam", "Epic Games", "GOG Games",
    };

    public static List<InstalledProgram> Find(IReadOnlyList<InstalledProgram> installed)
    {
        var known = installed.Select(p => p.InstallLocation.Trim('"', ' ')).Where(l => l.Length > 3)
                             .Select(PathUtil.Normalize).ToList();
        var self = PathUtil.Normalize(AppContext.BaseDirectory);
        var roots = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                roots.Add(d.RootDirectory.FullName);
        }
        catch (Exception) { }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(profile, "PortableApps"));
        roots.Add(Path.Combine(profile, "Apps"));
        roots.Add(Path.Combine(profile, "Tools"));

        var result = new List<InstalledProgram>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in PathUtil.Dirs(root))
            {
                var name = Path.GetFileName(dir);
                if (Skip.Contains(name) || name.StartsWith('$') || name.StartsWith('.') || FileMover.IsReparsePoint(dir)) continue;
                var folder = PathUtil.Normalize(dir);
                if (PathUtil.SameOrUnder(folder, self) || PathUtil.IsUnder(self, folder)) continue;
                // Belongs to (or contains) an installed program → not portable.
                if (known.Any(k => PathUtil.SameOrUnder(k, folder) || PathUtil.IsUnder(folder, k))) continue;
                if (!SafePaths.IsRemovableFolder(folder, out _)) continue;
                string[] exes;
                try { exes = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly); }
                catch (Exception) { continue; }
                var main = exes.Where(e => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(e),
                    "unins|setup|install|update|crash|helper|vc_?redist|dxsetup", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .ToList();
                if (main.Count == 0) continue;
                var info = Publisher(main[0]);
                result.Add(new InstalledProgram
                {
                    Name = name,
                    Publisher = info.Publisher,
                    Version = info.Version,
                    InstallLocation = folder,
                    DisplayIcon = main[0],
                    RegistryKey = "portable:" + folder,
                    IsPortable = true,
                    InstallDate = SafeCreation(folder),
                });
            }
        }
        return result;
    }

    private static (string Publisher, string Version) Publisher(string exe)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return ((v.CompanyName ?? "").Trim(), (v.ProductVersion ?? "").Trim());
        }
        catch (Exception) { return ("", ""); }
    }

    private static DateTime? SafeCreation(string folder)
    {
        try { return Directory.GetCreationTime(folder); } catch (Exception) { return null; }
    }
}
