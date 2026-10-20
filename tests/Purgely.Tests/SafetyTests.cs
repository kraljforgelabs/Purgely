using System.IO;
using Microsoft.Win32;
using Purgely.Core;
using Purgely.Core.Startup;
using Xunit;

namespace Purgely.Tests;

public class CriticalKeyTests
{
    [Theory]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Classes")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Policies")]
    [InlineData(@"HKLM\SOFTWARE\Classes\WOW6432Node\PROTOCOLS")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKLM\SOFTWARE\Classes\CLSID")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\shellex\DragDropHandlers")]
    public void Structural_windows_keys_are_never_deletable(string key) =>
        Assert.True(Cleaner.IsCriticalKey(key));

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\7-Zip")]
    [InlineData(@"HKCU\Software\IObit\Advanced SystemCare")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip")]
    public void Program_keys_are_deletable(string key) =>
        Assert.False(Cleaner.IsCriticalKey(key));
}

public class KeywordTests
{
    [Theory]
    [InlineData("Notepad++ 8.6 (64-bit x64)", "notepad")]
    [InlineData("Advanced SystemCare 17", "advanced systemcare")]
    [InlineData("VLC media player", "vlc media player")]
    public void CleanName_removes_versions_and_architecture(string input, string expected) =>
        Assert.Equal(expected, Keywords.CleanName(input));

    [Theory]
    [InlineData("Adobe Systems Inc.", "adobe")]
    [InlineData("Intel(R) Corporation", "intel")]
    [InlineData("Intel® Corporation", "intel")]
    [InlineData("Google LLC", "google")]
    public void CleanPublisher_normalizes_company_names(string input, string expected) =>
        Assert.Equal(expected, Keywords.CleanPublisher(input));

    [Fact]
    public void Generic_target_names_produce_no_strong_keywords()
    {
        var target = ScanTarget.FromName("Microsoft");
        Assert.DoesNotContain("microsoft", target.Strong);
        Assert.True(target.IsEmpty);
    }
}

public class WingetParserTests
{
    private static string Row(string name, string id, string version, string available, string source) =>
        name.PadRight(22) + id.PadRight(22) + version.PadRight(12) + available.PadRight(12) + source;

    [Fact]
    public void Parses_upgrade_table()
    {
        var output = string.Join("\n",
            "   - \r   \\ \r" + Row("Name", "Id", "Version", "Available", "Source"),
            new string('-', 80),
            Row("Google Chrome", "Google.Chrome", "129.0.1", "130.0.2", "winget"),
            Row("Bonjour", "Apple.Bonjour", "3.0.0.10", "3.1.0.1", "winget"),
            "2 upgrades available.");
        var updates = WingetUpdates.Parse(output);
        Assert.NotNull(updates);
        Assert.Equal(2, updates!.Count);
        Assert.Equal("Google.Chrome", updates[0].Id);
        Assert.Equal("130.0.2", updates[0].Available);
        Assert.Equal("Bonjour", updates[1].Name);
    }

    [Fact]
    public void No_updates_gives_empty_list() =>
        Assert.Empty(WingetUpdates.Parse("No installed package found matching input criteria.")!);

    [Fact]
    public void Unknown_language_gives_null()
    {
        var output = "Naziv            ID          Verzija    Dostupno   Izvor\n" + new string('-', 60) + "\nx  y  z  w  v";
        Assert.Null(WingetUpdates.Parse(output));
    }
}

public class SuiteDetectorTests
{
    private static InstalledProgram P(string name, string publisher, DateTime? installed, string key) =>
        new() { Name = name, Publisher = publisher, InstallDate = installed, RegistryKey = key };

    [Fact]
    public void Finds_the_rest_of_the_solidworks_suite_but_not_unrelated_programs()
    {
        var day = new DateTime(2026, 2, 10);
        var publisher = "Dassault Systemes SolidWorks Corp";
        var programs = new List<InstalledProgram>
        {
            P("SOLIDWORKS Visualize 2026", publisher, day, "k1"),
            P("3DEXPERIENCE Exchange for SOLIDWORKS", publisher, day, "k2"),
            P("eDrawings 2026", publisher, new DateTime(2025, 1, 1), "k3"),
            P("Google Chrome", "Google LLC", day, "k4"),
            P("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", "Microsoft Corporation", day, "k5"),
        };

        var related = SuiteDetector.FindRelated("SOLIDWORKS 2026 SP0.1", publisher, day, "", programs, "main");

        Assert.Contains(related, r => r.Name.StartsWith("SOLIDWORKS Visualize") && r.IsChecked);
        Assert.Contains(related, r => r.Name.StartsWith("3DEXPERIENCE Exchange") && r.IsChecked);
        Assert.DoesNotContain(related, r => r.Name == "Google Chrome");
        Assert.DoesNotContain(related, r => r.Name.Contains("Visual C++"));
        Assert.DoesNotContain(related, r => r.Name == "eDrawings 2026" && r.IsChecked);
    }

    [Fact]
    public void Company_name_alone_does_not_select_every_product()
    {
        var day = new DateTime(2024, 6, 4);
        var programs = new List<InstalledProgram>
        {
            P("AutoCAD Open in Desktop", "Autodesk", day, "k1"),
            P("Autodesk Access", "Autodesk, Inc.", new DateTime(2025, 4, 14), "k2"),
        };
        var related = SuiteDetector.FindRelated("Autodesk AutoCAD 2023 - English", "Autodesk, Inc.", day, "", programs, "main");
        Assert.Contains(related, r => r.Name == "AutoCAD Open in Desktop");
        Assert.DoesNotContain(related, r => r.Name == "Autodesk Access" && r.IsChecked);
    }
}

public class StartupKnowledgeTests
{
    [Theory]
    [InlineData("Steam", @"C:\Program Files (x86)\Steam\steam.exe", StartupSafety.Safe)]
    [InlineData("RtkAudUService", @"C:\Windows\System32\RtkAudUService64.exe", StartupSafety.Keep)]
    [InlineData("Avast", @"C:\Program Files\Avast Software\Avast\AvastUI.exe", StartupSafety.Keep)]
    [InlineData("OneDrive", @"C:\Users\x\AppData\Local\Microsoft\OneDrive\OneDrive.exe", StartupSafety.Check)]
    public void Known_entries_have_the_expected_safety(string name, string target, StartupSafety expected) =>
        Assert.Equal(expected, StartupKnowledge.Find(name, target)?.Safety);

    [Theory]
    [InlineData("ResetTool", @"C:\Tools\reset.exe")]
    [InlineData("Backup Agent", @"C:\Tools\backup.exe")]
    public void Unrelated_names_do_not_match(string name, string target) =>
        Assert.Null(StartupKnowledge.Find(name, target));
}

public class RuntimeTests
{
    [Theory]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", true)]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.40664", true)]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.10 (x64)", true)]
    [InlineData("Google Chrome", false)]
    public void Runtime_names_are_recognized(string name, bool expected) =>
        Assert.Equal(expected, DependencyMap.IsRuntimeName(name));

    [Fact]
    public void Reads_imports_of_a_windows_executable()
    {
        var pe = PeReader.Read(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        Assert.NotNull(pe);
        Assert.Equal("x64", pe!.Architecture);
        Assert.NotEmpty(pe.Imports);
    }
}

public class RegValueRoundTripTests
{
    [Fact]
    public void All_value_kinds_survive_save_and_restore()
    {
        const string path = @"Software\PurgelyTests";
        using var key = Registry.CurrentUser.CreateSubKey(path, true);
        try
        {
            key.SetValue("", "default text", RegistryValueKind.String);
            key.SetValue("expand", @"%SystemRoot%\x.exe", RegistryValueKind.ExpandString);
            key.SetValue("multi", new[] { "a", "b c" }, RegistryValueKind.MultiString);
            key.SetValue("emptyMulti", Array.Empty<string>(), RegistryValueKind.MultiString);
            key.SetValue("bin", new byte[] { 0, 1, 255 }, RegistryValueKind.Binary);
            key.SetValue("dword", unchecked((int)0xFFFFFFF0), RegistryValueKind.DWord);
            key.SetValue("qword", long.MaxValue, RegistryValueKind.QWord);

            foreach (var name in new[] { "", "expand", "multi", "emptyMulti", "bin", "dword", "qword" })
            {
                var saved = RegValue.Read(key, name);
                Assert.NotNull(saved);
                saved!.WriteTo(key, name + "_copy");
                var copy = RegValue.Read(key, name + "_copy");
                Assert.True(saved.SameAs(copy), $"{name} did not round-trip");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }
}

// ---------------------------------------------------------------- council review, round 1
public class SafePathTests
{
    private static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    [Fact]
    public void Windows_user_folders_and_their_parents_are_never_removable()
    {
        var profile = F(Environment.SpecialFolder.UserProfile);
        foreach (var path in new[]
                 {
                     F(Environment.SpecialFolder.Windows), Path.Combine(F(Environment.SpecialFolder.Windows), "System32", "drivers"),
                     profile, Path.GetDirectoryName(profile)!, F(Environment.SpecialFolder.MyDocuments),
                     Path.Combine(profile, "Downloads"), Path.Combine(profile, "Downloads", "SomeApp"),
                     F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.LocalApplicationData),
                     @"C:\", @"\\server\share\app",
                 })
            Assert.False(SafePaths.IsRemovableFolder(path, out _), path);
    }

    [Fact]
    public void A_normal_program_folder_is_removable()
    {
        var path = Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "Some Vendor", "Some App");
        Assert.True(SafePaths.IsRemovableFolder(path, out var why), why);
    }

    [Fact]
    public void Folders_directly_on_a_drive_are_shallow() => Assert.True(SafePaths.IsShallow(@"D:\Games"));
}

public class FileMoverTests
{
    [Fact]
    public void Move_to_backup_and_back_restores_every_file_and_never_overwrites_newer_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "PurgelyTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "app");
        var backup = Path.Combine(root, "undo", "001", "app");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "a.txt"), "A");
            File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "B");

            var result = FileMover.MoveToBackup(source, backup, isFolder: true);
            Assert.True(result.BackedUp);
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Combine(backup, "sub", "b.txt")));

            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "NEWER");
            FileMover.MoveBack(backup, source, isFolder: true);
            Assert.Equal("NEWER", File.ReadAllText(Path.Combine(source, "a.txt")));
            Assert.Equal("B", File.ReadAllText(Path.Combine(source, "sub", "b.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}

public class CouncilRegressionTests
{
    private static InstalledProgram P(string name, string publisher, DateTime? installed, string key) =>
        new() { Name = name, Publisher = publisher, InstallDate = installed, RegistryKey = key };

    [Fact]
    public void Store_package_keys_are_critical() =>
        Assert.True(Cleaner.IsCriticalKey(@"HKCU\Software\Classes\Microsoft.WindowsCalculator_8wekyb3d8bbwe"));

    [Theory]
    [InlineData("Acer Incorporated", "acer")]
    [InlineData("Logitech Group", "logitech")]
    public void More_company_suffixes_are_removed(string input, string expected) =>
        Assert.Equal(expected, Keywords.CleanPublisher(input));

    [Fact]
    public void Same_brand_on_the_same_day_is_not_ticked()
    {
        var day = new DateTime(2024, 1, 5);
        var programs = new List<InstalledProgram>
        {
            P("Acer Quick Access", "Acer Incorporated", day, "k1"),
            P("Acer Purified Voice Console", "Acer Incorporated", day, "k2"),
        };
        var related = SuiteDetector.FindRelated("Acer Care Center", "Acer Incorporated", day, "", programs, "main");
        Assert.DoesNotContain(related, r => r.IsChecked);
    }

    [Fact]
    public void Visual_studio_code_does_not_tick_visual_studio()
    {
        var day = new DateTime(2025, 3, 3);
        var programs = new List<InstalledProgram>
        {
            P("Visual Studio Community 2022", "Microsoft Corporation", day, "k1"),
        };
        var related = SuiteDetector.FindRelated("Microsoft Visual Studio Code (User)", "Microsoft Corporation", day, "", programs, "main");
        Assert.DoesNotContain(related, r => r.IsChecked);
    }

    [Theory]
    [InlineData("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", StartupSafety.Keep)]
    [InlineData("ZoomText", @"C:\Program Files\Freedom Scientific\ZoomText\ZoomText.exe", StartupSafety.Keep)]
    [InlineData("TeamViewer", @"C:\Program Files\TeamViewer\TeamViewer.exe", StartupSafety.Check)]
    [InlineData("Logi Options+", @"C:\Program Files\LogiOptionsPlus\logioptionsplus_agent.exe", StartupSafety.Keep)]
    [InlineData("Veeam Agent", @"C:\Program Files\Veeam\Endpoint Backup\Veeam.EndPoint.Tray.exe", StartupSafety.Keep)]
    public void Things_people_depend_on_are_never_marked_safe(string name, string target, StartupSafety expected) =>
        Assert.Equal(expected, StartupKnowledge.Find(name, target)?.Safety);
}

public class CompileReviewRegressionTests
{
    [Fact]
    public void Teams_is_not_mistaken_for_steam() =>
        Assert.Equal("Microsoft Teams.", StartupKnowledge.Find("MSTeams", @"C:\Users\x\AppData\Local\Microsoft\Teams\ms-teams.exe")?.Description);

    [Fact]
    public void Cached_windows_installer_files_stay_removable_but_nothing_else_in_windows()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(SafePaths.IsRemovableFile(Path.Combine(windows, "Installer", "1a2b3c.msi"), out var why), why);
        Assert.False(SafePaths.IsRemovableFile(Path.Combine(windows, "System32", "kernel32.dll"), out _));
        Assert.False(SafePaths.IsRemovableFile(Path.Combine(windows, "Installer", "sub", "x.msi"), out _));
    }
}

public class SweepRegressionTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\Foo\app.exe"" /run", @"C:\Program Files\Foo\app.exe")]
    [InlineData(@"C:\Program Files\Foo.Batch Tools\x.exe -a", @"C:\Program Files\Foo.Batch Tools\x.exe")]
    [InlineData(@"C:\Program Files\Some Tool\runner -x", "")]
    public void Executable_is_parsed_safely_or_unknown(string command, string expected) =>
        Assert.Equal(expected, PathUtil.ExtractExecutable(command));
}

public class BloatwareTests
{
    private static InstalledProgram Store(string package, string name) => new()
    {
        Name = name, RegistryKey = $"appx:{package}_8wekyb3d8bbwe", IsStoreApp = true, PackageName = package,
        PackageFullName = $"{package}_1.0.0.0_x64__8wekyb3d8bbwe",
    };

    private static InstalledProgram Program(string name) => new() { Name = name, RegistryKey = $@"HKLM\X\{name}" };

    [Fact]
    public void Ads_apps_are_ticked_but_essentials_never_are()
    {
        var items = Bloatware.Scan(new[]
        {
            Store("Microsoft.BingNews", "News"), Store("Microsoft.WindowsStore", "Microsoft Store"),
            Store("Microsoft.DesktopAppInstaller", "App Installer"), Store("Microsoft.XboxGamingOverlay", "Game Bar"),
        });
        Assert.True(items.Single(i => i.Name == "News").IsChecked);
        Assert.All(items.Where(i => i.Rating == BloatRating.Keep), i => Assert.False(i.IsChecked));
        Assert.False(items.Single(i => i.Name == "Game Bar").IsChecked);
        Assert.Equal(BloatRating.Keep, items.Single(i => i.Name == "App Installer").Rating);
    }

    [Fact]
    public void Program_names_match_whole_words_only()
    {
        var items = Bloatware.Scan(new[] { Program("Norton Security"), Program("Nortonville Tools"), Program("Microsoft.BingNews") });
        Assert.Single(items);
        Assert.Equal("Norton Security", items[0].Name);
        Assert.False(items[0].IsChecked); // antivirus: offered, never ticked
    }

    [Fact]
    public void Known_rules_never_point_into_user_files()
    {
        Assert.NotNull(KnownPrograms.For("Microsoft OneDrive"));
        Assert.Null(KnownPrograms.For("OneDrive Photo Helper"));
        Assert.Equal("", KnownPrograms.Expand(@"%USERPROFILE%\OneDrive\Documents"));
        Assert.Equal("", KnownPrograms.Expand(@"%NO_SUCH_VARIABLE_PURGELY%\x"));
        foreach (var rule in KnownPrograms.Rules)
            foreach (var path in rule.ProgramPaths.Concat(rule.UserDataPaths))
                Assert.DoesNotContain("%USERPROFILE%", path, StringComparison.OrdinalIgnoreCase);
    }
}

public class KnowledgeBaseTests
{
    [Fact]
    public void Built_in_knowledge_files_load_and_are_safe()
    {
        Assert.True(KnownPrograms.Rules.Length >= 15);
        foreach (var rule in KnownPrograms.Rules)
            foreach (var path in rule.ProgramPaths.Concat(rule.UserDataPaths))
            {
                Assert.DoesNotContain("%USERPROFILE%\\Documents", path, StringComparison.OrdinalIgnoreCase);
                Assert.False(path.TrimEnd('\\').Count(c => c == '\\') < 1, $"{rule.Program}: '{path}' is too shallow");
            }
        // A Keep or Check app is never ticked, whatever the JSON says.
        var items = Bloatware.Scan(new[]
        {
            new InstalledProgram { Name = "Store", RegistryKey = "appx:x", IsStoreApp = true, PackageName = "Microsoft.WindowsStore" },
            new InstalledProgram { Name = "Get Help", RegistryKey = "appx:y", IsStoreApp = true, PackageName = "Microsoft.GetHelp" },
        });
        Assert.All(items, i => Assert.False(i.IsChecked));
    }
}

public class DriverCatalogTests
{
    [Theory]
    [InlineData("Intel - Net - 23.60.0.10", "Intel")]
    [InlineData("Intel Corporation Bluetooth Driver Update (24.70.0.4)", "Intel Corporation")]
    [InlineData("Realtek Semiconductor Corp. Net Driver Update (10.68.0.0)", "Realtek Semiconductor Corp.")]
    public void Vendor_is_read_from_both_title_styles(string title, string vendor) =>
        Assert.Equal(vendor, new CatalogDriver("x", title, "Windows 11", "Drivers", null, "1.0", "1 MB", "id", true).Vendor);
}

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.9.0", "0.9.0")]
    [InlineData("0.9.1-beta", "0.9.1")]
    [InlineData("v1.0", "1.0.0")]
    [InlineData("V2.3.4", "2.3.4")]
    public void Release_tags_are_read_as_versions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.Parse(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void Odd_tags_are_ignored(string tag) => Assert.Null(UpdateCheck.Parse(tag));
}

public class PathVariableTests
{
    [Theory]
    [InlineData(@"C:\Tools\Foo\bin", @"c:\tools\foo\bin\")]
    [InlineData(@"%ProgramFiles%\Foo", @"%PROGRAMFILES%\Foo")]
    [InlineData(@" C:\Foo ", @"C:\Foo")]
    public void Same_entry_ignores_case_spaces_and_trailing_backslash(string a, string b) =>
        Assert.True(PathVariable.SameEntry(a, b));

    [Fact]
    public void Different_entries_are_not_the_same() =>
        Assert.False(PathVariable.SameEntry(@"C:\Foo", @"C:\Foo\bin"));
}

public class NewLeftoverKindTests
{
    [Theory]
    [InlineData(LeftoverKind.ContextMenu, true)]
    [InlineData(LeftoverKind.FileType, true)]
    [InlineData(LeftoverKind.AppPath, true)]
    [InlineData(LeftoverKind.RegistryKey, true)]
    [InlineData(LeftoverKind.FirewallRule, false)]
    [InlineData(LeftoverKind.PathEntry, false)]
    [InlineData(LeftoverKind.Folder, false)]
    public void Registration_kinds_are_removed_as_registry_keys(LeftoverKind kind, bool isKey) =>
        Assert.Equal(isKey, new LeftoverItem { Kind = kind, Location = @"HKLM\SOFTWARE\Classes\Foo.File" }.IsRegistryKeyKind);

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes\*\shell\Open with Foo")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Foo.Document")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foo.exe")]
    [InlineData(@"HKCU\Software\Classes\Directory\shellex\ContextMenuHandlers\FooMenu")]
    public void Registration_keys_of_a_program_are_deletable(string key) =>
        Assert.False(Cleaner.IsCriticalKey(key));

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes\*\shell")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths")]
    public void Registration_parents_are_never_deletable(string key) =>
        Assert.True(Cleaner.IsCriticalKey(key));
}

public class RepairTests
{
    [Theory]
    [InlineData(28, "No driver")]
    [InlineData(22, "turned off")]
    [InlineData(43, "stopped the device")]
    [InlineData(999, "problem code 999")]
    public void Problem_codes_are_explained_in_plain_words(int code, string expected) =>
        Assert.Contains(expected, Repair.Explain(code).Meaning, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("SCSIAdapter")]
    [InlineData("System")]
    [InlineData("Display")]
    [InlineData("DiskDrive")]
    public void Boot_critical_devices_are_never_restarted_or_reinstalled(string cls)
    {
        var device = new HardwareDevice { DeviceId = @"PCI\VEN_1234&DEV_5678\1", Class = cls, Name = "x", ProblemCode = 10 };
        var problem = Repair.FindProblems(new[] { device }).Single();
        Assert.False(problem.CanRestart);
        Assert.False(problem.CanReinstall);
    }

    [Fact]
    public void Usb_controllers_are_never_reinstalled()
    {
        var device = new HardwareDevice { DeviceId = @"PCI\VEN_8086&DEV_43ED\3&11583659&0&A0", Class = "USB", Name = "USB controller", ProblemCode = 10 };
        Assert.False(Repair.FindProblems(new[] { device }).Single().CanReinstall);
    }
}

public class CatalogIdTests
{
    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_9A60&SUBSYS_14771025&REV_01", @"PCI\VEN_8086&DEV_9A60")]
    [InlineData(@"PCI\VEN_8086&DEV_9A60&SUBSYS_14771025", @"PCI\VEN_8086&DEV_9A60")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0295&SUBSYS_10251477&REV_1000", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0295")]
    [InlineData(@"PCI\VEN_8086&DEV_9A60&CC_030000", "")]
    [InlineData(@"PCI\VEN_8086&DEV_9A60", "")]
    [InlineData(@"USB\VID_8087&PID_0026&REV_0002", "")]
    public void Chip_level_id_is_derived_only_from_board_specific_ids(string id, string expected) =>
        Assert.Equal(expected, DriverCatalog.BaseId(id));
}
