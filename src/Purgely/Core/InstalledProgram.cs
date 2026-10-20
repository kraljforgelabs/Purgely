namespace Purgely.Core;

/// <summary>A program registered in one of Windows' "Uninstall" registry locations.</summary>
public sealed class InstalledProgram
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public long? SizeKb { get; init; }
    public DateTime? InstallDate { get; init; }
    /// <summary>Exact time Windows last wrote the uninstall entry (install or update time).</summary>
    public DateTime? RegisteredAt { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string DisplayIcon { get; init; } = "";

    /// <summary>Full registry path, e.g. HKLM\SOFTWARE\...\Uninstall\{GUID}.</summary>
    public required string RegistryKey { get; init; }

    public bool IsMsi { get; init; }
    public string? MsiProductCode { get; init; }

    /// <summary>Non-empty when the program was removed but its registration stayed behind.</summary>
    public string OrphanReason { get; set; } = "";
    public bool IsOrphan => OrphanReason.Length > 0;
    /// <summary>Not in Windows' list any more – remembered by the Timeline so its leftovers can still be found.</summary>
    public bool IsRemovedEarlier { get; init; }

    /// <summary>A Microsoft Store (AppX/MSIX) app; RegistryKey is "appx:&lt;PackageFamilyName&gt;".</summary>
    public bool IsStoreApp { get; init; }
    /// <summary>Store apps: the package to remove (PackageFullName) and its short name (e.g. Microsoft.BingNews).</summary>
    public string PackageFullName { get; init; } = "";
    public string PackageName { get; init; } = "";
    /// <summary>A gentle hint from <see cref="Suggestions"/> (unused for months, one of several similar programs).</summary>
    public string Suggestion { get; set; } = "";
    public bool HasSuggestion => Suggestion.Length > 0;
    /// <summary>A folder with a program that was never installed (RegistryKey is "portable:&lt;folder&gt;").</summary>
    public bool IsPortable { get; init; }
    /// <summary>A game from a launcher that doesn't register it with Windows (e.g. "Epic Games"); RegistryKey is "epic:&lt;AppName&gt;".</summary>
    public string Launcher { get; init; } = "";
    public bool IsLauncherGame => Launcher.Length > 0;
    /// <summary>Launcher games: the launcher's file for this game (gone once the launcher has uninstalled it).</summary>
    public string LauncherManifest { get; init; } = "";
    public string StatusText => IsOrphan ? OrphanReason : Suggestion.Length > 0 ? "Tip: " + Suggestion
        : IsStoreApp ? "Microsoft Store app" : IsPortable ? "Portable – not installed, just a folder"
        : IsLauncherGame ? $"{Launcher} game – uninstalled in the {Launcher} launcher" : "";

    /// <summary>Ticked in the program list for a bulk uninstall.</summary>
    public bool IsChecked { get; set; }

    /// <summary>True for Visual C++ / .NET runtimes (Dependency Map).</summary>
    public bool IsRuntime { get; set; }
    /// <summary>Installed programs found to need this runtime.</summary>
    public List<string> UsedBy { get; set; } = new();
    public string UsedByText => !IsRuntime ? ""
        : UsedBy.Count == 0 ? "No user found (may still be needed)"
        : UsedBy.Count == 1 ? $"Used by {UsedBy[0]}"
        : $"Used by {UsedBy.Count} programs";

    public string? UsedByTooltip => UsedBy.Count == 0 ? null : "Needed by:\n" + string.Join("\n", UsedBy);

    /// <summary>winget package id and newer version, when an update is available.</summary>
    public string? WingetId { get; set; }
    public string? AvailableVersion { get; set; }
    /// <summary>winget could not update this program last time (it must be updated by the publisher's own tool).</summary>
    public bool WingetFailed { get; set; }
    public string UpdateText => AvailableVersion is null ? ""
        : WingetFailed ? $"⬆ {AvailableVersion} (use publisher's updater)" : $"⬆ {AvailableVersion}";

    public string SizeText => SizeKb is > 0
        ? SizeKb.Value >= 1024 * 1024
            ? $"{SizeKb.Value / 1024d / 1024d:0.0} GB"
            : $"{SizeKb.Value / 1024d:0.0} MB"
        : "";

    public string InstallDateText => InstallDate?.ToString("yyyy-MM-dd") ?? "";
}
