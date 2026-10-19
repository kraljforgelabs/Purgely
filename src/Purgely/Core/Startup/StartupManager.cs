using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core.Startup;

public sealed class ManagedEntry
{
    public string Name { get; set; } = "";
    public bool KeepDisabled { get; set; }
    /// <summary>Original service start type (2 = automatic, 3 = manual), saved before Purgely changed it.</summary>
    public int? OriginalServiceStart { get; set; }
    public bool OriginalDelayedAuto { get; set; }
    /// <summary>Path of the Purgely scheduled task that starts a delayed item.</summary>
    public string? DelayedTask { get; set; }
}

public sealed class StartupState
{
    public HashSet<string> Known { get; set; } = new();
    public Dictionary<string, ManagedEntry> Managed { get; set; } = new();
    public List<string> LastOptimize { get; set; } = new();
    public DateTime? FirstChange { get; set; }
}

/// <summary>Switches startup items on/off/delayed – always in a reversible way – and remembers what it did.</summary>
public static class StartupManager
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely", "startup.json");

    public static StartupState LoadState()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<StartupState>(File.ReadAllText(FilePath)) ?? new StartupState();
            }
            catch (Exception) { }
            return new StartupState();
        }
    }

    private static void SaveState(StartupState state)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { }
        }
    }

    private static ManagedEntry Managed(StartupState state, StartupItem item)
    {
        if (!state.Managed.TryGetValue(item.Id, out var m))
            state.Managed[item.Id] = m = new ManagedEntry { Name = item.Name };
        return m;
    }

    // ------------------------------------------------------------------ actions
    public static (bool Ok, string Message) Disable(StartupItem item)
    {
        var state = LoadState();
        var m = Managed(state, item);
        var (ok, message) = item.Kind switch
        {
            StartupKind.RunKey or StartupKind.StartupFolder => RemoveDelayThenApprove(m, item, false),
            StartupKind.ScheduledTask => Run("schtasks", $"/Change /TN \"{item.Location}\" /DISABLE"),
            StartupKind.Service => SetService(item, m, "demand"),
            _ => (false, "Unknown item type"),
        };
        if (ok)
        {
            item.Enabled = false;
            item.Delayed = false;
            state.FirstChange ??= DateTime.Now;
            Timeline.Log(TimelineEventType.StartupChanged, item.Name, "Turned off at Windows startup");
        }
        SaveState(state);
        return (ok, message);
    }

    public static (bool Ok, string Message) Enable(StartupItem item)
    {
        var state = LoadState();
        var m = Managed(state, item);
        (bool, string) result;
        switch (item.Kind)
        {
            case StartupKind.RunKey or StartupKind.StartupFolder:
                result = RemoveDelayThenApprove(m, item, true);
                break;
            case StartupKind.ScheduledTask:
                result = Run("schtasks", $"/Change /TN \"{item.Location}\" /ENABLE");
                break;
            case StartupKind.Service:
                var mode = m.OriginalDelayedAuto ? "delayed-auto" : m.OriginalServiceStart == 3 ? "demand" : "auto";
                result = SetService(item, m, mode);
                break;
            default:
                result = (false, "Unknown item type");
                break;
        }
        if (result.Item1)
        {
            item.Enabled = true;
            item.Delayed = item.Kind == StartupKind.Service && m.OriginalDelayedAuto;
            item.KeepDisabled = false;
            m.KeepDisabled = false;
            Timeline.Log(TimelineEventType.StartupChanged, item.Name, "Turned back on at Windows startup");
        }
        SaveState(state);
        return result;
    }

    /// <summary>Starts the item 2 minutes after sign-in instead of immediately.</summary>
    public static (bool Ok, string Message) Delay(StartupItem item)
    {
        var state = LoadState();
        var m = Managed(state, item);
        (bool Ok, string Message) result;
        switch (item.Kind)
        {
            case StartupKind.Service:
                result = SetService(item, m, "delayed-auto");
                break;
            case StartupKind.RunKey or StartupKind.StartupFolder:
                var command = item.Kind == StartupKind.StartupFolder ? $"explorer.exe \"{item.ShortcutPath}\"" : item.Command;
                var safeName = string.Concat(item.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
                if (safeName.Length == 0) safeName = "Item";
                var taskName = $@"\Purgely\Delayed\{safeName}";
                var user = WindowsIdentity.GetCurrent().Name;
                result = Run("schtasks",
                    $"/Create /TN \"{taskName}\" /SC ONLOGON /DELAY 0002:00 /TR \"{command.Replace("\"", "\\\"")}\" /RU \"{user}\" /IT /F");
                if (result.Ok)
                {
                    var approved = SetApproved(item, false);
                    if (!approved.Ok)
                    {
                        Run("schtasks", $"/Delete /TN \"{taskName}\" /F");
                        result = approved;
                    }
                    else m.DelayedTask = taskName;
                }
                break;
            default:
                result = (false, "Scheduled tasks cannot be delayed here – turn them off or leave them on.");
                break;
        }
        if (result.Ok)
        {
            item.Enabled = true;
            item.Delayed = true;
            item.KeepDisabled = false;
            m.KeepDisabled = false;
            state.FirstChange ??= DateTime.Now;
            Timeline.Log(TimelineEventType.StartupChanged, item.Name, "Now starts 2 minutes after sign-in");
        }
        SaveState(state);
        return result;
    }

    public static void SetKeepDisabled(StartupItem item, bool keep)
    {
        var state = LoadState();
        Managed(state, item).KeepDisabled = keep;
        item.KeepDisabled = keep;
        SaveState(state);
    }

    // ------------------------------------------------------------------ guard checks
    /// <summary>
    /// "Keep off" items that are on again. Purgely does NOT switch them off by itself (you may have turned
    /// them on yourself, in Task Manager or in the program's own settings) – the home screen asks first.
    /// </summary>
    public static List<StartupItem> FindReEnabled(List<StartupItem> items) =>
        items.Where(i => i.KeepDisabled && i.Enabled && !i.Delayed).ToList();

    /// <summary>Turns "Keep off" items off again after the user agreed. Returns how many.</summary>
    public static int TurnOffAgain(IEnumerable<StartupItem> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            if (Disable(item).Ok) count++; // Disable() already writes the Timeline entry
        }
        return count;
    }

    /// <summary>Stop keeping these off (the user wants them on).</summary>
    public static void ForgetKeepOff(IEnumerable<StartupItem> items)
    {
        foreach (var item in items) SetKeepDisabled(item, false);
    }

    /// <summary>Items that appeared since Purgely last looked. The very first run only remembers what exists.</summary>
    public static List<StartupItem> FindNew(List<StartupItem> items)
    {
        var state = LoadState();
        var firstRun = state.Known.Count == 0;
        var fresh = firstRun ? new List<StartupItem>() : items.Where(i => i.Enabled && !state.Known.Contains(i.Id)).ToList();
        state.Known = items.Select(i => i.Id).ToHashSet();
        SaveState(state);
        foreach (var item in fresh)
            Timeline.Log(TimelineEventType.StartupChanged, item.Name, $"Added itself to Windows startup ({item.Program})");
        return fresh;
    }

    // ------------------------------------------------------------------ optimize
    public static List<StartupItem> OptimizeCandidates(IEnumerable<StartupItem> items) =>
        // Only entries the built-in knowledge base knows are safe – never a guess.
        items.Where(i => i.Enabled && !i.Delayed && i.Safety == StartupSafety.Safe && i.SafeByKnowledge).ToList();

    public static int Optimize(List<StartupItem> candidates)
    {
        var done = new List<string>();
        foreach (var item in candidates)
        {
            if (!Disable(item).Ok) continue;
            SetKeepDisabled(item, true);
            done.Add(item.Id);
        }
        var state = LoadState();
        state.LastOptimize = done;
        SaveState(state);
        return done.Count;
    }

    public static int UndoOptimize(List<StartupItem> items)
    {
        var state = LoadState();
        var ids = state.LastOptimize.ToHashSet();
        var count = 0;
        foreach (var item in items.Where(i => ids.Contains(i.Id)))
            if (Enable(item).Ok) count++;
        state = LoadState();
        state.LastOptimize.Clear();
        SaveState(state);
        return count;
    }

    public static bool CanUndoOptimize() => LoadState().LastOptimize.Count > 0;

    // ------------------------------------------------------------------ low level
    /// <summary>Windows' own on/off switch (StartupApproved) – exactly what Task Manager changes.</summary>
    private static (bool Ok, string Message) SetApproved(StartupItem item, bool enabled)
    {
        try
        {
            using var key = RegistryPath.Create(item.ApprovedKey);
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool Ok, string Message) SetService(StartupItem item, ManagedEntry m, string mode)
    {
        if (m.OriginalServiceStart is null)
        {
            using var key = RegistryPath.Open($@"HKLM\SYSTEM\CurrentControlSet\Services\{item.Location}");
            m.OriginalServiceStart = key?.GetValue("Start") is int s ? s : 2;
            m.OriginalDelayedAuto = key?.GetValue("DelayedAutostart") is 1;
        }
        return Run("sc", $"config \"{item.Location}\" start= {mode}");
    }

    private static (bool Ok, string Message) RemoveDelayThenApprove(ManagedEntry m, StartupItem item, bool enabled)
    {
        if (m.DelayedTask is { Length: > 0 } task) Run("schtasks", $"/Delete /TN \"{task}\" /F");
        m.DelayedTask = null;
        return SetApproved(item, enabled);
    }

    private static (bool Ok, string Message) Run(string file, string args)
    {
        var (code, output) = Shell.Run(file, args, 30_000);
        if (code == 0) return (true, output);
        // Error 5 from the service manager: the program protects its own service (common for Store/MSIX apps
        // such as ChatGPT or Claude). Explain it instead of showing raw "[SC] OpenService FAILED 5".
        if (code == 5 || System.Text.RegularExpressions.Regex.IsMatch(output, @"\bFAILED 5\b") ||
            output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return (false, "Windows doesn't allow changing this service – the program protects it. " +
                           "Use the program's own setting (e.g. \"Start with Windows\") instead.");
        return (false, output.Trim());
    }
}
