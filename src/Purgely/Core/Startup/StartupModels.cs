using System.ComponentModel;

namespace Purgely.Core.Startup;

public enum StartupKind { RunKey, StartupFolder, ScheduledTask, Service }
public enum StartupImpact { None, Low, Medium, High }
public enum StartupSafety { Safe, Check, Keep }

/// <summary>One thing that starts with Windows.</summary>
public sealed class StartupItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required StartupKind Kind { get; init; }
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public string TargetPath { get; init; } = "";
    /// <summary>Registry key, startup folder, task path or service name.</summary>
    public string Location { get; init; } = "";
    /// <summary>Run value name, shortcut file name or service name.</summary>
    public string ValueName { get; init; } = "";
    /// <summary>Windows' own on/off switch for Run and startup-folder items (the one Task Manager uses).</summary>
    public string ApprovedKey { get; init; } = "";
    public string ShortcutPath { get; init; } = "";
    public bool AllUsers { get; init; }

    public string Program { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Signer { get; set; } = "";
    public string Description { get; set; } = "";
    public StartupImpact Impact { get; set; }
    public string ImpactReason { get; set; } = "";
    public StartupSafety Safety { get; set; }
    public string SafetyReason { get; set; } = "";
    public string SuspiciousReason { get; set; } = "";
    /// <summary>Safe because the built-in knowledge base says so (not a guess) – only these are used by Optimize.</summary>
    public bool SafeByKnowledge { get; set; }
    public bool IsSuspicious => SuspiciousReason.Length > 0;
    public bool TargetMissing { get; set; }

    private bool _enabled, _delayed, _keepDisabled;
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
    public bool Delayed { get => _delayed; set { _delayed = value; Changed(); } }
    public bool KeepDisabled { get => _keepDisabled; set { _keepDisabled = value; Changed(); } }

    public string StatusText => Delayed ? "⏱ Delayed" : Enabled ? "● On" : KeepDisabled ? "○ Off (kept off)" : "○ Off";

    public string KindText => Kind switch
    {
        StartupKind.RunKey => "Registry",
        StartupKind.StartupFolder => "Startup folder",
        StartupKind.ScheduledTask => "Scheduled task",
        StartupKind.Service => "Service",
        _ => Kind.ToString(),
    } + (AllUsers ? " · all users" : "");

    public string ImpactText => Impact switch
    {
        StartupImpact.High => "High",
        StartupImpact.Medium => "Medium",
        StartupImpact.Low => "Low",
        _ => "None",
    };

    public string SafetyText => Safety switch
    {
        StartupSafety.Safe => "✔ Safe to disable",
        StartupSafety.Check => "? Check first",
        _ => "✖ Keep it on",
    };

    public string Details => string.Join("  •  ", new[]
    {
        IsSuspicious ? "⚠ " + SuspiciousReason : "", Description, SafetyReason, ImpactReason,
    }.Where(s => s.Length > 0));

    public string Tooltip => $"{Command}\n{(Signer.Length > 0 ? "Signed by " + Signer : "Not digitally signed")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
