using System.ComponentModel;

namespace Purgely.Core;

public enum LeftoverKind { Folder, File, Shortcut, RegistryKey, RegistryValue, StartupEntry, Service, Driver, ScheduledTask }

/// <summary>One thing a program left behind, with the evidence that links it to the program.</summary>
public sealed class LeftoverItem : INotifyPropertyChanged
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>Folder/file path, registry key path, service name or task path.</summary>
    public required string Location { get; init; }

    /// <summary>Friendly name (service display name, task name…).</summary>
    public string Detail { get; set; } = "";
    /// <summary>The executable a service/task/startup entry runs.</summary>
    public string TargetPath { get; set; } = "";
    /// <summary>For startup entries: the registry key and value name.</summary>
    public string RegistryKeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    /// <summary>For registry values reverted by Trial Mode: the value to put back (null = remove it).</summary>
    public RegValue? OldValue { get; set; }

    public int Score { get; private set; }
    public List<string> Evidence { get; } = new();
    public string SharedWith { get; set; } = "";
    /// <summary>Boot or system driver (Start 0/1) – never pre-selected.</summary>
    public bool BootStart { get; set; }
    public bool IsShared => SharedWith.Length > 0;
    /// <summary>Auto-start mechanism that could bring the program back.</summary>
    public bool IsZombie { get; set; }
    /// <summary>Pre-selected by Purgely ("Select recommended").</summary>
    public bool Recommended { get; set; }
    /// <summary>Set by the cleaner when the item was removed.</summary>
    public bool Removed { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public void AddEvidence(int points, string why)
    {
        if (Evidence.Contains(why)) return;
        Evidence.Add(why);
        Score = Math.Clamp(Score + points, 0, 99);
    }

    public string KindText => (IsZombie ? "Auto-start · " : "") + Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "File",
        LeftoverKind.Shortcut => "Shortcut",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.StartupEntry => "Startup entry",
        LeftoverKind.Service => "Service",
        LeftoverKind.Driver => "Driver",
        LeftoverKind.ScheduledTask => "Scheduled task",
        _ => Kind.ToString(),
    };

    public long SizeBytes { get; set; }
    public string SizeText => Sizes.Format(SizeBytes);

    /// <summary>Confidence in words first ("Very sure · 88%"), so beginners don't have to interpret a number.</summary>
    public string ScoreText => IsShared ? "Keep (shared)" : $"{ConfidenceWord} · {Score}%";

    public string ConfidenceWord => Score switch { >= 85 => "Very sure", >= 70 => "Likely", >= 45 => "Maybe", _ => "Unsure" };

    public string DisplayLocation => Kind switch
    {
        LeftoverKind.Service or LeftoverKind.Driver =>
            Detail.Length > 0 && Detail != Location ? $"{Detail}  ({Location})" : Location,
        LeftoverKind.StartupEntry => $"{RegistryKeyPath}  →  {ValueName}",
        LeftoverKind.RegistryValue => $"{RegistryKeyPath}  →  {(ValueName.Length == 0 ? "(Default)" : ValueName)}",
        _ => Location,
    };

    public string Why => IsShared
        ? $"Also used by {SharedWith} – keeping it."
        : string.Join("  •  ", Evidence);

    public event PropertyChangedEventHandler? PropertyChanged;
}
