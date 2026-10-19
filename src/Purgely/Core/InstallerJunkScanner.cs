using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// Hidden Installer Junk: cached installers in C:\Windows\Installer and C:\ProgramData\Package Cache
/// that no installed program or update uses any more.
/// </summary>
public static partial class InstallerJunkScanner
{
    private const string UserDataPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    public static List<LeftoverItem> Scan(IReadOnlyList<InstalledProgram> programs)
    {
        var items = new List<LeftoverItem>();
        ScanWindowsInstaller(items);
        ScanPackageCache(items, programs);
        foreach (var item in items)
        {
            item.SizeBytes = Sizes.Of(item);
            item.Recommended = item.Score >= 70;
            item.IsChecked = item.Recommended;
        }
        return items.OrderByDescending(i => i.Score).ThenByDescending(i => i.SizeBytes).ToList();
    }

    /// <summary>.msi/.msp files that Windows Installer no longer references.</summary>
    private static void ScanWindowsInstaller(List<LeftoverItem> items)
    {
        var folder = Path.Combine(PathUtil.WindowsDir, "Installer");
        if (!Directory.Exists(folder)) return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = hklm.OpenSubKey(UserDataPath);
            if (userData is null) return;
            foreach (var sid in userData.GetSubKeyNames())
            {
                using var products = userData.OpenSubKey($@"{sid}\Products");
                foreach (var product in products?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var props = products!.OpenSubKey($@"{product}\InstallProperties");
                    if (props?.GetValue("LocalPackage") is string pkg) referenced.Add(PathUtil.Normalize(pkg));
                }
                using var patches = userData.OpenSubKey($@"{sid}\Patches");
                foreach (var patch in patches?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var key = patches!.OpenSubKey(patch);
                    if (key?.GetValue("LocalPackage") is string pkg) referenced.Add(PathUtil.Normalize(pkg));
                }
            }
        }
        catch (Exception) { return; }

        // Safety: if we could not read any references, we cannot tell what is orphaned.
        if (referenced.Count == 0) return;

        foreach (var file in PathUtil.Files(folder, "*.ms?"))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".msi" or ".msp")) continue;
            if (referenced.Contains(PathUtil.Normalize(file))) continue;
            var item = new LeftoverItem { Kind = LeftoverKind.File, Location = file };
            item.AddEvidence(85, ext == ".msp"
                ? "Cached update that no installed program uses any more"
                : "Cached installer of a program that is no longer installed");
            items.Add(item);
        }
    }

    /// <summary>Package Cache folders whose package is no longer installed.</summary>
    private static void ScanPackageCache(List<LeftoverItem> items, IReadOnlyList<InstalledProgram> programs)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        if (!Directory.Exists(root)) return;

        var installed = InstalledProductCodes(programs);
        var providers = DependencyProviders();
        var references = UninstallReferences();
        if (installed.Count == 0) return; // safety

        foreach (var dir in PathUtil.Dirs(root))
        {
            var name = Path.GetFileName(dir);
            if (providers.Contains(name)) continue;
            if (references.Contains(dir.ToLowerInvariant())) continue;

            var guid = GuidRegex().Match(name);
            var item = new LeftoverItem { Kind = LeftoverKind.Folder, Location = dir };
            if (guid.Success)
            {
                var code = guid.Value.ToUpperInvariant();
                if (installed.Contains(code) || providers.Contains(code)) continue;
                item.AddEvidence(80, "Cached installer of a package that is no longer installed");
            }
            else
            {
                item.AddEvidence(40, "No installed program refers to it – check before removing");
            }
            items.Add(item);
        }
    }

    private static HashSet<string> InstalledProductCodes(IReadOnlyList<InstalledProgram> programs)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs.Where(p => !p.IsOrphan))
        {
            var last = p.RegistryKey.Split('\\').Last();
            if (GuidRegex().IsMatch(last)) codes.Add(last.ToUpperInvariant());
        }
        // Every product Windows Installer knows about (including hidden ones).
        foreach (var packed in RegistryPath.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Products"))
            if (Unpack(packed) is { } guid) codes.Add(guid);
        foreach (var sid in RegistryPath.SubKeys(RegistryHive.LocalMachine, UserDataPath))
            foreach (var packed in RegistryPath.SubKeys(RegistryHive.LocalMachine, $@"{UserDataPath}\{sid}\Products"))
                if (Unpack(packed) is { } guid) codes.Add(guid);
        // Hidden uninstall entries (e.g. bundle packages) also count.
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            foreach (var sub in RegistryPath.SubKeys(RegistryHive.LocalMachine, path))
                if (GuidRegex().IsMatch(sub)) codes.Add(GuidRegex().Match(sub).Value.ToUpperInvariant());
        return codes;
    }

    /// <summary>Burn "dependency providers" – packages other bundles still rely on.</summary>
    private static HashSet<string> DependencyProviders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RegistryPath.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Dependencies"))
        {
            set.Add(name);
            var g = GuidRegex().Match(name);
            if (g.Success) set.Add(g.Value.ToUpperInvariant());
        }
        return set;
    }

    /// <summary>All text in uninstall entries (to see which cache folders are referenced).</summary>
    private static string UninstallReferences()
    {
        var sb = new StringBuilder();
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            foreach (var sub in RegistryPath.SubKeys(hive, path))
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey($@"{path}\{sub}");
                    if (key is null) continue;
                    foreach (var value in key.GetValueNames())
                        sb.Append(key.GetValue(value)?.ToString()).Append('\n');
                }
                catch (Exception) { }
            }
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Windows Installer stores product codes "packed" – this converts them back to {GUID}.</summary>
    private static string? Unpack(string packed)
    {
        if (packed.Length != 32 || !packed.All(Uri.IsHexDigit)) return null;
        static string Rev(string s) => new(s.Reverse().ToArray());
        static string Swap(string s) => string.Concat(Enumerable.Range(0, s.Length / 2).Select(i => $"{s[i * 2 + 1]}{s[i * 2]}"));
        var p = packed.ToUpperInvariant();
        return $"{{{Rev(p[..8])}-{Rev(p[8..12])}-{Rev(p[12..16])}-{Swap(p[16..20])}-{Swap(p[20..32])}}}";
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidRegex();
}
