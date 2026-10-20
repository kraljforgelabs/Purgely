using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// Games from launchers that don't add them to Windows' list of programs. Today that is the Epic Games Launcher: it
/// keeps one small JSON manifest (*.item) per installed game. (Steam, GOG, EA, Ubisoft and Battle.net register their
/// games with Windows, so those are already in the normal list.)
/// Read-only: such a game is uninstalled in its launcher, so the launcher knows it is gone. Purgely opens the launcher
/// and afterwards scans for what the game left behind.
/// </summary>
public static class LauncherGames
{
    public const string Epic = "Epic Games";

    public static List<InstalledProgram> List()
    {
        var result = new List<InstalledProgram>();
        var folder = EpicManifestFolder();
        if (folder is null || !Directory.Exists(folder)) return result;
        string[] files;
        try { files = Directory.GetFiles(folder, "*.item", SearchOption.TopDirectoryOnly); }
        catch (Exception) { return result; }

        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > 1_000_000) continue; // not a manifest
                if (ReadEpicManifest(File.ReadAllText(file), file) is { } game) result.Add(game);
            }
            catch (Exception) { /* unreadable or broken manifest – skip it */ }
        }
        return result.GroupBy(p => p.RegistryKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>One Epic manifest → a game, or null for add-ons (DLC), broken entries and games whose folder is gone.</summary>
    public static InstalledProgram? ReadEpicManifest(string json, string manifestPath, bool checkFolder = true)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) return null;
        string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        var app = S("AppName");
        var main = S("MainGameAppName");
        if (app.Length == 0) return null;
        if (main.Length > 0 && !main.Equals(app, StringComparison.OrdinalIgnoreCase)) return null; // DLC – part of its game
        var location = S("InstallLocation").Replace('/', '\\').TrimEnd('\\');
        if (location.Length <= 3 || !Path.IsPathFullyQualified(location) || location.Contains("..")) return null;
        if (checkFolder && !Directory.Exists(location)) return null;

        var name = S("DisplayName");
        long? sizeKb = e.TryGetProperty("InstallSize", out var s) && s.ValueKind == JsonValueKind.Number &&
                       s.TryGetInt64(out var bytes) && bytes > 0 ? bytes / 1024 : null;
        var exe = S("LaunchExecutable").Replace('/', '\\');
        var icon = exe.Length > 0 && !Path.IsPathRooted(exe) && !exe.Contains("..") ? Path.Combine(location, exe) : "";
        var (publisher, _) = checkFolder && icon.Length > 0 && File.Exists(icon) ? FileInfoOf(icon) : ("", "");

        return new InstalledProgram
        {
            Name = name.Length > 0 ? name : app,
            Publisher = publisher,
            Version = S("AppVersionString"),
            SizeKb = sizeKb,
            InstallLocation = location,
            DisplayIcon = icon,
            RegistryKey = "epic:" + app,
            Launcher = Epic,
            LauncherManifest = manifestPath,
            InstallDate = checkFolder ? SafeCreation(location) : null,
        };
    }

    /// <summary>True while the launcher still lists the game (its manifest and folder are both there).</summary>
    public static bool StillInstalled(InstalledProgram game) =>
        game.LauncherManifest.Length > 0 && File.Exists(game.LauncherManifest) && Directory.Exists(game.InstallLocation);

    /// <summary>
    /// Opens the launcher as the signed-in user (through Explorer – Purgely runs as administrator, and a game launcher
    /// should never be started with those rights). False when the launcher isn't installed.
    /// </summary>
    public static bool OpenLauncher(InstalledProgram game)
    {
        if (game.Launcher != Epic || EpicLauncherExe() is not { } exe) return false;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string? EpicManifestFolder()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            if (key?.GetValue("AppDataPath") is string data && data.Length > 3 && Path.IsPathFullyQualified(data))
                return Path.Combine(data, "Manifests");
        }
        catch (Exception) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
    }

    /// <summary>The launcher's .exe – only from Program Files, never a path some other program wrote somewhere.</summary>
    private static string? EpicLauncherExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (root.Length == 0) continue;
            foreach (var bits in new[] { "Win64", "Win32" })
            {
                var exe = Path.Combine(root, "Epic Games", "Launcher", "Portal", "Binaries", bits, "EpicGamesLauncher.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    private static (string Publisher, string Version) FileInfoOf(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return ((v.CompanyName ?? "").Trim(), (v.ProductVersion ?? "").Trim());
        }
        catch (Exception) { return ("", ""); }
    }

    private static DateTime? SafeCreation(string folder)
    {
        try { return Directory.GetCreationTime(folder); } catch (Exception) { return null; }
    }
}
