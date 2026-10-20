using System.IO;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// The optional "Uninstall with Purgely" entry in the right-click menu of programs, shortcuts and folders (on
/// Windows 11 under "Show more options"). Off until the user turns it on in Settings; only Purgely's own keys under
/// HKCU\Software\Classes are written, and turning it off (or removing Purgely) deletes exactly those keys.
/// </summary>
public static class ExplorerMenu
{
    private const string Verb = "PurgelyUninstall";
    private const string Label = "Uninstall with Purgely";
    /// <summary>.exe files, shortcuts, internet shortcuts (Steam/Epic games) and folders.</summary>
    private static readonly string[] Classes = { "exefile", "lnkfile", "InternetShortcut", "Directory" };

    private static string KeyPath(string cls) => $@"Software\Classes\{cls}\shell\{Verb}";

    public static bool IsOn()
    {
        try
        {
            return Classes.All(cls =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath(cls) + @"\command");
                return key is not null;
            });
        }
        catch (Exception) { return false; }
    }

    /// <summary>The command the entry runs: this Purgely.exe with the clicked item.</summary>
    public static string CommandFor(string exe) => $"\"{exe}\" --uninstall \"%1\"";

    /// <summary>Only a real Purgely.exe on a local drive (not a network share, not a path with quotes).</summary>
    public static bool CanUse(string? exe) =>
        exe is { Length: > 3 } && Path.IsPathFullyQualified(exe) && !exe.StartsWith(@"\\") && !exe.Contains('"') &&
        !exe.Contains('%') && Path.GetFileName(exe).Equals("Purgely.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe);

    public static void TurnOn(string exe)
    {
        if (!CanUse(exe)) throw new InvalidOperationException("The right-click entry needs Purgely.exe on a local drive.");
        try
        {
            foreach (var cls in Classes)
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath(cls));
                key.SetValue("", Label);
                key.SetValue("Icon", $"\"{exe}\",0");
                using var command = key.CreateSubKey("command");
                command.SetValue("", CommandFor(exe));
            }
        }
        catch (Exception)
        {
            TurnOff(); // all or nothing – never half an entry
            throw;
        }
    }

    public static void TurnOff()
    {
        foreach (var cls in Classes)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath(cls), throwOnMissingSubKey: false); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Purgely.exe was moved: point the entry at where it is now – only when the Purgely.exe it points to is gone, so
    /// starting another copy (e.g. a test version in Downloads) doesn't quietly take the entry over.
    /// </summary>
    public static void FollowMove(string? exe)
    {
        if (!CanUse(exe)) return;
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey(KeyPath("exefile") + @"\command");
            if (command?.GetValue("") is not string current || current == CommandFor(exe!)) return;
            var old = current.StartsWith('"') && current.IndexOf('"', 1) is > 1 and var end ? current[1..end] : "";
            if (old.Length > 0 && !File.Exists(old)) TurnOn(exe!);
        }
        catch (Exception) { }
    }
}
