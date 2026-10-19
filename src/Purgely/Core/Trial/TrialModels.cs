using System.Text.Json.Serialization;

namespace Purgely.Core.Trial;

public enum TrialState { Installing, Analyzed, Kept, Reverted }

public sealed class RegValueChange
{
    /// <summary>Full key path, e.g. HKLM\SOFTWARE\Vendor.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The value before the install. Null = the value did not exist (it was added).</summary>
    public RegValue? Old { get; set; }
    /// <summary>The value right after the install (null for deleted values or older records).</summary>
    public RegValue? New { get; set; }
}

/// <summary>Everything an installer changed, found by comparing "before" and "after".</summary>
public sealed class TrialDiff
{
    public List<string> AddedDirectories { get; set; } = new();
    public List<string> AddedFiles { get; set; } = new();
    public int ModifiedFiles { get; set; }
    public List<string> ModifiedFileSamples { get; set; } = new();
    public List<string> AddedRegistryKeys { get; set; } = new();
    public List<RegValueChange> ValueChanges { get; set; } = new();
    public List<RegValueChange> DeletedValues { get; set; } = new();
    public List<string> AddedServices { get; set; } = new();
    public List<string> AddedTasks { get; set; } = new();
    /// <summary>Uninstall keys that appeared – the program(s) the installer registered.</summary>
    public List<string> NewPrograms { get; set; } = new();
    public List<string> NewProgramNames { get; set; } = new();
    public bool PossibleBundle { get; set; }
}

public sealed class TrialRecord
{
    public string Id { get; set; } = "";
    public string Installer { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Analyzed { get; set; }
    public TrialState State { get; set; }
    public bool RestorePointCreated { get; set; }
    /// <summary>Number of files/folders in the "before" snapshot – used to estimate progress later.</summary>
    public int FilesBefore { get; set; }
    public TrialDiff? Diff { get; set; }

    [JsonIgnore] public string Title => Diff is { NewProgramNames.Count: > 0 } d
        ? string.Join(" + ", d.NewProgramNames)
        : System.IO.Path.GetFileName(Installer);
    [JsonIgnore] public string StartedText => Started.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string StateText => State switch
    {
        TrialState.Installing => "Waiting for the installer to finish",
        TrialState.Analyzed => "On trial – keep or revert",
        TrialState.Kept => "Kept",
        TrialState.Reverted => "Reverted",
        _ => State.ToString(),
    };
    [JsonIgnore] public string ChangesText => Diff is not { } d ? "" :
        $"{d.AddedDirectories.Count} folders, {d.AddedFiles.Count} files, {d.AddedRegistryKeys.Count} keys, " +
        $"{d.ValueChanges.Count + d.DeletedValues.Count} values, {d.AddedServices.Count} services, {d.AddedTasks.Count} tasks";
}
