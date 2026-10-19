using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Purgely.Core.Startup;

/// <summary>Finds everything that starts with Windows and rates each item.</summary>
public static partial class StartupScanner
{
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static readonly (string Key, string Approved, bool AllUsers)[] RunKeys =
    {
        (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", $@"HKCU\{ApprovedRoot}\Run", false),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{ApprovedRoot}\Run", true),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{ApprovedRoot}\Run32", true),
    };

    public static List<StartupItem> Scan(IReadOnlyList<InstalledProgram> programs)
    {
        var state = StartupManager.LoadState();
        var items = new List<StartupItem>();
        ScanRunKeys(items);
        ScanStartupFolders(items);
        ScanTasks(items);
        ScanServices(items, state);

        Dictionary<string, int> slow;
        try { slow = BootTimes.SlowStartupApps(); } catch (Exception) { slow = new(); }

        foreach (var item in items)
        {
            if (state.Managed.TryGetValue(item.Id, out var managed))
            {
                item.KeepDisabled = managed.KeepDisabled;
                if (managed.DelayedTask is { Length: > 0 } task && File.Exists(TaskStore.Root + task))
                {
                    item.Delayed = true;
                    item.Enabled = true;
                }
            }
            Evaluate(item, programs, slow);
        }
        return items.OrderBy(i => i.Program, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name).ToList();
    }

    // ------------------------------------------------------------------ sources
    private static void ScanRunKeys(List<StartupItem> items)
    {
        foreach (var (keyPath, approved, allUsers) in RunKeys)
        {
            try
            {
                using var key = RegistryPath.Open(keyPath);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var command = key.GetValue(name)?.ToString() ?? "";
                    if (name.Length == 0 || command.Length == 0) continue;
                    items.Add(new StartupItem
                    {
                        Id = $"run|{keyPath}|{name}",
                        Kind = StartupKind.RunKey,
                        Name = name,
                        Command = command,
                        TargetPath = PathUtil.ExtractExecutable(command),
                        Location = keyPath,
                        ValueName = name,
                        ApprovedKey = approved,
                        AllUsers = allUsers,
                        Enabled = IsApproved(approved, name),
                    });
                }
            }
            catch (Exception) { }
        }
    }

    private static void ScanStartupFolders(List<StartupItem> items)
    {
        var folders = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), $@"HKCU\{ApprovedRoot}\StartupFolder", false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), $@"HKLM\{ApprovedRoot}\StartupFolder", true),
        };
        foreach (var (folder, approved, allUsers) in folders)
        {
            if (folder.Length == 0 || !Directory.Exists(folder)) continue;
            foreach (var file in PathUtil.Files(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) ?? file : file;
                items.Add(new StartupItem
                {
                    Id = $"folder|{file}",
                    Kind = StartupKind.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = target,
                    TargetPath = target,
                    Location = folder,
                    ValueName = fileName,
                    ApprovedKey = approved,
                    ShortcutPath = file,
                    AllUsers = allUsers,
                    Enabled = IsApproved(approved, fileName),
                });
            }
        }
    }

    private static void ScanTasks(List<StartupItem> items)
    {
        var root = TaskStore.Root;
        foreach (var file in TaskStore.AllTaskFiles())
        {
            var taskPath = file[root.Length..];
            if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
                taskPath.StartsWith(@"\Purgely\", StringComparison.OrdinalIgnoreCase)) continue;
            string xml;
            try { xml = File.ReadAllText(file); } catch (Exception) { continue; }
            if (!xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase) &&
                !xml.Contains("<BootTrigger", StringComparison.OrdinalIgnoreCase)) continue;

            var command = Unescape(CommandRegex().Match(xml).Groups[1].Value.Trim());
            var arguments = Unescape(ArgumentsRegex().Match(xml).Groups[1].Value.Trim());
            var settings = SettingsRegex().Match(xml).Groups[1].Value;
            var enabled = !settings.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
            items.Add(new StartupItem
            {
                Id = $"task|{taskPath}",
                Kind = StartupKind.ScheduledTask,
                Name = CleanTaskName(Path.GetFileName(file)),
                Command = (command + " " + arguments).Trim(),
                TargetPath = PathUtil.ExtractExecutable(command),
                Location = taskPath,
                ValueName = taskPath,
                AllUsers = true,
                Enabled = enabled,
            });
        }
    }

    private static void ScanServices(List<StartupItem> items, StartupState state)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var key = services.OpenSubKey(name);
                    if (key is null) continue;
                    var type = key.GetValue("Type") is int t ? t : 0;
                    if ((type & 0x30) == 0) continue; // not a normal Windows service (drivers etc.)
                    var start = key.GetValue("Start") is int s ? s : 3;
                    var id = $"service|{name}";
                    var managedByUs = state.Managed.ContainsKey(id);
                    if (start != 2 && !managedByUs) continue; // only automatic services (or ones Purgely switched)

                    var image = key.GetValue("ImagePath")?.ToString() ?? "";
                    var exe = PathUtil.ExtractExecutable(image);
                    if (exe.Length == 0 || IsMicrosoft(exe)) continue;
                    var display = key.GetValue("DisplayName")?.ToString() ?? name;
                    if (display.StartsWith('@')) display = name;

                    items.Add(new StartupItem
                    {
                        Id = id,
                        Kind = StartupKind.Service,
                        Name = display,
                        Command = image,
                        TargetPath = exe,
                        Location = name,
                        ValueName = name,
                        AllUsers = true,
                        Enabled = start == 2,
                        Delayed = start == 2 && key.GetValue("DelayedAutostart") is 1,
                    });
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ rating
    private static void Evaluate(StartupItem item, IReadOnlyList<InstalledProgram> programs, Dictionary<string, int> slow)
    {
        var target = item.TargetPath;
        item.TargetMissing = target.Length > 0 && Path.IsPathRooted(target) && !File.Exists(target);

        FileVersionInfo? info = null;
        if (!item.TargetMissing && File.Exists(target))
        {
            try { info = FileVersionInfo.GetVersionInfo(target); } catch (Exception) { }
            item.Signer = SignerOf(target);
        }
        item.Publisher = info?.CompanyName?.Trim() ?? "";

        var owner = programs.FirstOrDefault(p => !p.IsOrphan && p.InstallLocation.Length > 3 &&
                                                 PathUtil.IsUnder(target, PathUtil.Normalize(p.InstallLocation)));
        item.Program = owner?.Name
                       ?? (info?.ProductName is { Length: > 0 } product ? product.Trim() : null)
                       ?? (item.Publisher.Length > 0 ? item.Publisher : item.Name);

        // Match on the item name AND the program it belongs to ("DB Bigupgrade Task" → Driver Booster → IObit).
        var known = StartupKnowledge.Find($"{item.Name} {item.Program}", target);
        if (known is not null && !item.TargetMissing) item.Description = known.Description;

        // Suspicious?
        if (!item.TargetMissing && item.Signer.Length == 0 && target.Length > 0)
        {
            var dir = Path.GetDirectoryName(target) ?? "";
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var temp = Path.GetTempPath().TrimEnd('\\');
            if (PathUtil.SameOrUnder(dir, temp))
                item.SuspiciousReason = "Unsigned program started from the Temp folder – typical for unwanted software";
            else if (new[] { local, roaming, programData }.Any(r => dir.Equals(r, StringComparison.OrdinalIgnoreCase)))
                item.SuspiciousReason = "Unsigned program placed directly in AppData/ProgramData – check what it is";
            else if (RandomNameRegex().IsMatch(Path.GetFileNameWithoutExtension(target)))
                item.SuspiciousReason = "Unsigned program with a random-looking name – check what it is";
        }

        // Impact on startup
        var fileName = Path.GetFileName(target);
        long size = 0;
        try { if (File.Exists(target)) size = new FileInfo(target).Length; } catch (Exception) { }
        if (item.TargetMissing)
        {
            item.Impact = StartupImpact.None;
            item.ImpactReason = "The program it starts no longer exists";
        }
        else if (fileName.Length > 0 && slow.TryGetValue(fileName, out var times))
        {
            item.Impact = StartupImpact.High;
            item.ImpactReason = $"Windows logged it slowing down startup {times} time(s)";
        }
        else if (known?.Heavy == true)
        {
            item.Impact = StartupImpact.High;
            item.ImpactReason = "Large program that loads a lot at startup";
        }
        else if (item.Kind == StartupKind.Service)
        {
            item.Impact = item.Delayed ? StartupImpact.Low : StartupImpact.Medium;
            item.ImpactReason = item.Delayed ? "Background service, already started with a delay" : "Background service that runs all the time";
        }
        else if (size > 30 * 1024 * 1024)
        {
            item.Impact = StartupImpact.Medium;
            item.ImpactReason = $"Large program ({size / 1024 / 1024} MB)";
        }
        else
        {
            item.Impact = StartupImpact.Low;
            item.ImpactReason = "Small helper";
        }

        // Safe to disable?
        var hay = Keywords.Norm(item.Name + " " + Path.GetFileNameWithoutExtension(target));
        if (item.TargetMissing)
        {
            item.Safety = StartupSafety.Safe;
            item.SafetyReason = "Broken entry – it starts nothing";
            item.SafeByKnowledge = true;
        }
        else if (known is not null)
        {
            item.Safety = known.Safety;
            item.SafeByKnowledge = known.Safety == StartupSafety.Safe;
        }
        else if (item.IsSuspicious)
        {
            item.Safety = StartupSafety.Check;
        }
        else if (new[] { "update", "updater", "helper", "launcher", "tray", "notifier", "agent" }.Any(w => hay.Contains(w)))
        {
            // Only a hint: "agent" or "tray" can also be a backup, VPN or hotkey helper.
            item.Safety = StartupSafety.Check;
            item.SafetyReason = "Looks like an updater or helper – usually fine to turn off, but check what it belongs to";
        }
        else if (new[] { "realtek", "synaptics", "elan", "nvidia", "intel", "amd", "dolby", "waves", "conexant" }
                 .Any(v => item.Publisher.Contains(v, StringComparison.OrdinalIgnoreCase)))
        {
            item.Safety = StartupSafety.Check;
            item.SafetyReason = "Hardware driver helper – keep it unless you know what it does";
        }
        else
        {
            item.Safety = StartupSafety.Check;
            item.SafetyReason = "Probably safe – the program still works when you open it yourself";
        }
    }

    // ------------------------------------------------------------------ helpers
    public static bool IsApproved(string approvedKey, string valueName)
    {
        try
        {
            using var key = RegistryPath.Open(approvedKey);
            if (key?.GetValue(valueName) is byte[] data && data.Length > 0) return (data[0] & 1) == 0;
        }
        catch (Exception) { }
        return true;
    }

    private static bool IsMicrosoft(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return PathUtil.SameOrUnder(exe, PathUtil.WindowsDir);
            var company = FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "";
            return company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static string SignerOf(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            var cn = CnRegex().Match(cert.Subject);
            return cn.Success ? cn.Groups[1].Value.Trim('"', ' ') : cert.Subject;
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Reads a .lnk target through the Windows Script Host COM object.</summary>
    internal static string? ResolveShortcut(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            var shell = Activator.CreateInstance(shellType)!;
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
            var target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"Battle.net_Reg_HKCURun_S-1-5-21-…" → "Battle.net".</summary>
    private static string CleanTaskName(string name)
    {
        var cleaned = RegTaskSuffixRegex().Replace(name, "").Trim('_', ' ');
        return cleaned.Length > 0 ? cleaned : name;
    }

    private static string Unescape(string s) =>
        s.Replace("&quot;", "\"").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&apos;", "'");

    [GeneratedRegex(@"<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ArgumentsRegex();

    [GeneratedRegex(@"<Settings>(.*?)</Settings>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SettingsRegex();

    [GeneratedRegex(@"_Reg_HK\w+(_S-[\d-]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RegTaskSuffixRegex();

    [GeneratedRegex(@"CN=([^,]+)")]
    private static partial Regex CnRegex();

    [GeneratedRegex(@"^(?=(?:.*\d){3})[a-z0-9]{8,}$", RegexOptions.IgnoreCase)]
    private static partial Regex RandomNameRegex();
}
