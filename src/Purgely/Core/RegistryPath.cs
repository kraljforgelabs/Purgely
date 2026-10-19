using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>Helpers for "HKLM\..." / "HKCU\..." style registry paths (always the 64-bit view).</summary>
public static class RegistryPath
{
    public static (RegistryHive Hive, string SubKey) Parse(string path)
    {
        var slash = path.IndexOf('\\');
        var root = slash > 0 ? path[..slash] : path;
        var sub = slash > 0 ? path[(slash + 1)..] : "";
        var hive = root.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException($"Unknown registry root: {root}"),
        };
        return (hive, sub);
    }

    public static RegistryKey? Open(string path, bool writable = false)
    {
        var (hive, sub) = Parse(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return baseKey.OpenSubKey(sub, writable);
    }

    public static RegistryKey Create(string path)
    {
        var (hive, sub) = Parse(path);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return baseKey.CreateSubKey(sub, true);
    }

    public static bool Exists(string path)
    {
        try
        {
            using var key = Open(path);
            return key is not null;
        }
        catch (Exception) { return false; }
    }

    public static string[] SubKeys(RegistryHive hive, string sub)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(sub);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
