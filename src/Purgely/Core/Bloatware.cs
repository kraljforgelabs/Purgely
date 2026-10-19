using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core;

public enum BloatRating { Remove, Check, Keep }

/// <summary>A preinstalled or promotional app found on this PC, with a plain-language verdict.</summary>
public sealed class BloatItem : INotifyPropertyChanged
{
    public required InstalledProgram Program { get; init; }
    public required BloatRating Rating { get; init; }
    public required string Why { get; init; }
    public string Name => Program.Name;
    public string Maker => Program.Publisher;
    public string Kind => Program.IsStoreApp ? "Store app" : "Program";
    public string RatingText => Rating switch
    {
        BloatRating.Remove => "Safe to remove",
        BloatRating.Check => "Check – you may use it",
        _ => "Keep",
    };
    private bool _checked;
    public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>One "Windows clutter" switch (ads, AI features, privacy) made of registry values (mostly for the current user).</summary>
public sealed class ClutterTweak : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string What { get; init; }
    /// <summary>Values that turn the clutter OFF: (key, value name, DWORD).</summary>
    public required (string Key, string Value, int Off)[] Values { get; init; }
    public string Note { get; init; } = "";
    /// <summary>"Ads and suggestions", "AI features" or "Privacy" – the page groups by it.</summary>
    public string Category { get; init; } = "Ads and suggestions";
    /// <summary>Ticked by default when on (ads and privacy). AI features are the user's choice.</summary>
    public bool TickByDefault { get; init; } = true;

    /// <summary>True when the clutter is currently on (any value differs from its "off" setting).</summary>
    public bool IsOn { get; set; }
    /// <summary>The description plus any warning, for the Details column.</summary>
    public string Details => Note.Length > 0 ? $"{What} Note: {Note}" : What;
    public string StateText => IsOn ? "On" : "Off";
    private bool _checked;
    public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>
/// Bloatware and Windows clutter: a built-in knowledge base of preinstalled/promotional apps (with a verdict for
/// each) and per-user switches for ads and suggestions. Every switch is saved to an Undo package; Purgely
/// remembers what you turned off and tells you when Windows turns it back on.
/// </summary>
public static class Bloatware
{
    private sealed record Known(string[] Patterns, BloatRating Rating, string Why, bool Tick = false);

    private sealed class KbFile
    {
        public List<KbEntry> Apps { get; set; } = new();
    }

    private sealed class KbEntry
    {
        public string[] Patterns { get; set; } = Array.Empty<string>();
        public BloatRating Rating { get; set; }
        public string Why { get; set; } = "";
        public bool Tick { get; set; }
    }

    /// <summary>Knowledge/bloatware.json – store apps match by package-name prefix ("=Name" = exact), programs by whole words.</summary>
    private static readonly Lazy<Known[]> KbLazy = new(() =>
        KnowledgeBase.Load<KbFile>("bloatware.json").Apps
            .Where(e => e.Patterns.Length > 0)
            .Select(e => new Known(e.Patterns, e.Rating, e.Why, e.Tick && e.Rating == BloatRating.Remove))
            .ToArray());

    private static Known[] Kb => KbLazy.Value;

    /// <summary>Store apps match by package name prefix; programs by whole words in their display name.</summary>
    private static bool Matches(InstalledProgram p, string pattern)
    {
        if (pattern.StartsWith('=')) return p.IsStoreApp && p.PackageName.Equals(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (p.IsStoreApp) return p.PackageName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        if (pattern.Contains('.') && !pattern.Contains(' ') && !pattern.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
            return false; // a package name – only meaningful for Store apps
        return System.Text.RegularExpressions.Regex.IsMatch(p.Name, $@"(^|\W){System.Text.RegularExpressions.Regex.Escape(pattern)}(\W|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static List<BloatItem> Scan(IReadOnlyList<InstalledProgram> programs)
    {
        var result = new List<BloatItem>();
        foreach (var p in programs.Where(p => !p.IsOrphan && !p.IsRemovedEarlier && !p.IsPortable))
        {
            var known = Kb.FirstOrDefault(k => k.Patterns.Any(pattern => Matches(p, pattern)));
            if (known is null || known.Rating == BloatRating.Keep && !p.IsStoreApp) continue;
            result.Add(new BloatItem { Program = p, Rating = known.Rating, Why = known.Why, IsChecked = known.Tick && known.Rating == BloatRating.Remove });
        }
        return result.OrderBy(b => b.Rating).ThenBy(b => b.Name).ToList();
    }

    // ------------------------------------------------------------------ clutter switches
    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static List<ClutterTweak> Tweaks() => new()
    {
        new() { Id = "start-ads", Name = "Ads and recommendations in Start",
                What = "Promoted apps and 'recommended' suggestions in the Start menu.",
                Values = new[] { (Adv, "Start_IrisRecommendations", 0), (Cdm, "SubscribedContent-338388Enabled", 0), (Cdm, "SystemPaneSuggestionsEnabled", 0) } },
        new() { Id = "silent-apps", Name = "Apps installed without asking",
                What = "Windows quietly installs 'suggested' apps (games, social media).",
                Values = new[] { (Cdm, "SilentInstalledAppsEnabled", 0), (Cdm, "PreInstalledAppsEnabled", 0), (Cdm, "OemPreInstalledAppsEnabled", 0) } },
        new() { Id = "tips", Name = "Tips, suggestions and 'finish setting up' nags",
                What = "Pop-ups and Settings banners pushing Microsoft services.",
                Values = new[] { (Cdm, "SubscribedContent-338393Enabled", 0), (Cdm, "SubscribedContent-353694Enabled", 0),
                                 (Cdm, "SubscribedContent-353696Enabled", 0), (Cdm, "SoftLandingEnabled", 0),
                                 (@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0) } },
        new() { Id = "lockscreen", Name = "Ads and 'fun facts' on the lock screen",
                What = "Text and promotions over the lock screen picture.",
                Values = new[] { (Cdm, "RotatingLockScreenOverlayEnabled", 0), (Cdm, "SubscribedContent-338387Enabled", 0) } },
        new() { Id = "bing-search", Name = "Web (Bing) results in Start search",
                What = "Searching for a file or app also sends the text to Bing and mixes in web results.",
                Values = new[] { (@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1) } },
        new() { Id = "ad-id", Name = "Advertising ID",
                What = "An ID apps use to show you personalised ads.",
                Values = new[] { (@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0) } },
        new() { Id = "tailored", Name = "Tailored experiences",
                What = "Microsoft uses diagnostic data to show you tips and ads.",
                Values = new[] { (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0) } },
        new() { Id = "account-nags", Name = "Account notifications in Start",
                What = "Reminders in Start to sign in, back up or buy Microsoft 365.",
                Values = new[] { (Adv, "Start_AccountNotifications", 0) } },
        new() { Id = "widgets", Name = "Widgets (news) button on the taskbar", TickByDefault = false,
                What = "The weather/news panel on the taskbar.",
                Values = new[] { (Adv, "TaskbarDa", 0) },
                Note = "Turn it off in Settings → Personalization → Taskbar → Widgets." },

        // ---- AI features (the user's choice – not ticked by default, except Recall's snapshots)
        new() { Id = "recall", Name = "Recall snapshots", Category = "AI features",
                What = "Windows saves screenshots of what you do so you can search them later (Copilot+ PCs only).",
                Values = new[] { (@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1) } },
        new() { Id = "click-to-do", Name = "Click to Do", Category = "AI features", TickByDefault = false,
                What = "AI actions on whatever is on your screen (Win + click). Copilot+ PCs only.",
                Values = new[] { (@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1) } },
        new() { Id = "copilot", Name = "Copilot in Windows", Category = "AI features", TickByDefault = false,
                What = "The Copilot button and key. Turn off only if you don't use Microsoft's AI assistant.",
                Values = new[] { (@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1) },
                Note = "The newer Copilot app may ignore this – remove the Copilot app on the Preinstalled apps tab instead." },
        new() { Id = "edge-copilot", Name = "Copilot sidebar in Edge", Category = "AI features", TickByDefault = false,
                What = "The Copilot button and sidebar inside Microsoft Edge.",
                Values = new[] { (@"HKCU\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0) },
                Note = "Edge then shows \"Your browser is managed by your organization\" – that is this switch." },

        // ---- Privacy
        new() { Id = "telemetry", Name = "Optional diagnostic data", Category = "Privacy", TickByDefault = false,
                Note = "Settings then says \"Some settings are managed by your organization\" – that is this switch. Don't use it on Windows Insider builds (it blocks new builds).",
                What = "Limits what Windows sends to Microsoft to the required minimum.",
                Values = new[] { (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1) } },
        new() { Id = "typing", Name = "Typing and handwriting data", Category = "Privacy",
                What = "Windows learns from what you type and write to build a personal dictionary.",
                Values = new[] { (@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                                 (@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                                 (@"HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0) } },
        new() { Id = "activity", Name = "Activity history", Category = "Privacy",
                What = "Windows keeps a history of apps, files and sites you used.",
                Values = new[] { (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                                 (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0) } },
        new() { Id = "feedback", Name = "Feedback requests", Category = "Privacy",
                What = "Pop-ups asking you to rate Windows.",
                Values = new[] { (@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0) } },
    };

    /// <summary>Reads whether each switch is currently on.</summary>
    public static List<ClutterTweak> ReadTweaks()
    {
        var list = Tweaks();
        foreach (var t in list) t.IsOn = t.Values.Any(v => ReadDword(v.Key, v.Value) != v.Off);
        return list;
    }

    private static int? ReadDword(string key, string value)
    {
        try
        {
            using var k = RegistryPath.Open(key);
            return k?.GetValue(value) is int i ? i : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Turns the chosen clutter off. Every previous value is saved to one Undo package first. Returns per-tweak results.
    /// </summary>
    public static List<(ClutterTweak Tweak, bool Ok, string Message)> TurnOff(IReadOnlyList<ClutterTweak> tweaks)
    {
        using var busy = Activity.Begin();
        var package = UndoStore.CreatePackage("Windows clutter");
        var actions = new List<UndoAction>();
        var results = new List<(ClutterTweak, bool, string)>();
        foreach (var t in tweaks)
        {
            var before = actions.Count;
            try
            {
                foreach (var (key, value, off) in t.Values)
                {
                    using var k = RegistryPath.Create(key);
                    var old = RegValue.Read(k, value);
                    actions.Add(new UndoAction
                    {
                        Type = "Value", Original = key, Extra = value,
                        Backup = old is null ? "" : JsonSerializer.Serialize(old),
                    });
                    UndoStore.SaveActions(package, "Windows clutter turned off", actions); // saved before every change
                    k.SetValue(value, off, RegistryValueKind.DWord);
                }
                results.Add((t, true, "Turned off"));
                Timeline.Log(TimelineEventType.SettingChanged, t.Name, "Turned off – can be undone in the Undo center");
            }
            catch (Exception ex)
            {
                results.Add((t, false, ex is UnauthorizedAccessException ? "Windows protects this switch" : ex.Message));
                // Undo must only hold what really changed: drop this switch's actions. Values of this switch that
                // were already written are put back right away, so a half-applied switch never stays behind.
                var mine = actions.Skip(before).ToList();
                actions.RemoveRange(before, actions.Count - before);
                foreach (var a in mine.AsEnumerable().Reverse()) RestoreValue(a);
                try { UndoStore.SaveActions(package, "Windows clutter turned off", actions); } catch (Exception) { }
            }
        }
        if (actions.Count == 0) FileMover.DeleteBestEffort(package, true); // nothing changed – no empty Undo entry
        RememberOff(results.Where(r => r.Item2).Select(r => r.Item1.Id));
        return results;
    }

    private static void RestoreValue(UndoAction a)
    {
        try
        {
            using var key = RegistryPath.Create(a.Original);
            if (a.Backup.Length == 0) key.DeleteValue(a.Extra, false);
            else JsonSerializer.Deserialize<RegValue>(a.Backup)?.WriteTo(key, a.Extra);
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ "Windows turned it back on"
    private sealed class State
    {
        public HashSet<string> TweaksOff { get; set; } = new();
        public Dictionary<string, string> RemovedApps { get; set; } = new(); // package name → display name
    }

    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely", "bloatware.json");

    private static State Load()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new(); }
        catch (Exception) { return new(); }
    }

    private static void Save(State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(s));
        }
        catch (Exception) { }
    }

    private static void RememberOff(IEnumerable<string> ids)
    {
        var s = Load();
        s.TweaksOff.UnionWith(ids);
        Save(s);
    }

    public static void RememberRemoved(InstalledProgram app)
    {
        if (!app.IsStoreApp) return;
        var s = Load();
        s.RemovedApps[app.PackageName] = app.Name;
        Save(s);
    }

    /// <summary>Things the user removed or turned off that Windows brought back (for a home-screen notice).</summary>
    public static (List<ClutterTweak> Tweaks, List<InstalledProgram> Apps) Returned(IReadOnlyList<InstalledProgram> programs)
    {
        var s = Load();
        var tweaks = ReadTweaks().Where(t => t.IsOn && s.TweaksOff.Contains(t.Id)).ToList();
        var apps = programs.Where(p => p.IsStoreApp && s.RemovedApps.ContainsKey(p.PackageName)).ToList();
        return (tweaks, apps);
    }

    /// <summary>The user restored things through the Undo center: stop watching them.</summary>
    public static void ForgetRestored(IEnumerable<string> registryValues, IEnumerable<string> packageFamilies)
    {
        var s = Load();
        var values = registryValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tweaks().Where(t => t.Values.Any(v => values.Contains(v.Key + "|" + v.Value))))
            s.TweaksOff.Remove(t.Id);
        foreach (var family in packageFamilies)
        {
            var name = family.Split('_')[0];
            s.RemovedApps.Remove(name);
        }
        Save(s);
    }

    /// <summary>The user wants it back on / reinstalled it on purpose: stop watching it.</summary>
    public static void Forget(IEnumerable<ClutterTweak> tweaks, IEnumerable<InstalledProgram> apps)
    {
        var s = Load();
        foreach (var t in tweaks) s.TweaksOff.Remove(t.Id);
        foreach (var a in apps) s.RemovedApps.Remove(a.PackageName);
        Save(s);
    }
}
