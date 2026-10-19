using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace Purgely.Core.Trial;

/// <summary>Our own copy of a registry branch (used when Windows' "reg save" refuses to save it).</summary>
public sealed class RegSnapshot : Dictionary<string, Dictionary<string, RegValue>>
{
    public RegSnapshot() : base(StringComparer.OrdinalIgnoreCase) { }

    public void Save(string file)
    {
        using var fs = File.Create(file);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gz, (Dictionary<string, Dictionary<string, RegValue>>)this);
    }

    public static RegSnapshot Load(string file)
    {
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RegValue>>>(gz) ?? new();
        var snap = new RegSnapshot();
        foreach (var (key, values) in data)
            snap[key] = new Dictionary<string, RegValue>(values, StringComparer.OrdinalIgnoreCase);
        return snap;
    }
}

/// <summary>Compares a live registry branch with a saved copy (loaded hive or snapshot) and records what changed.</summary>
public sealed class RegistryDiff
{
    private readonly string _displayRoot;
    private readonly string[] _blocked;
    private readonly string[] _allowed;
    private readonly bool _compareValues;
    private readonly bool _topLevelOnly;
    private readonly Action<string> _onAddedKey;
    private readonly TrialDiff _diff;
    private readonly IProgress<string>? _progress;
    private readonly DateTime? _since;
    private int _visited;

    public RegistryDiff(string displayRoot, TrialDiff diff, Action<string> onAddedKey,
                        string[]? blocked = null, string[]? allowed = null,
                        bool compareValues = true, bool topLevelOnly = false, IProgress<string>? progress = null,
                        DateTime? since = null)
    {
        _displayRoot = displayRoot;
        _diff = diff;
        _onAddedKey = onAddedKey;
        _blocked = blocked ?? Array.Empty<string>();
        _allowed = allowed ?? Array.Empty<string>();
        _compareValues = compareValues;
        _topLevelOnly = topLevelOnly;
        _progress = progress;
        _since = since?.AddMinutes(-2);
    }

    /// <summary>
    /// Every registry key records when it was last written. A key the installer created or changed has a
    /// timestamp after the trial started; Windows' links and shared keys (e.g. WOW6432Node\Classes) do not.
    /// </summary>
    private bool WrittenSinceStart(RegistryKey key) =>
        _since is null || RegistryTimestamp.LastWrite(key) is { } written && written >= _since.Value;

    private bool IsReallyNew(RegistryKey parent, string name)
    {
        if (_since is null) return true;
        try
        {
            using var child = parent.OpenSubKey(name);
            return child is not null && WrittenSinceStart(child);
        }
        catch (Exception) { return false; }
    }

    private static bool Under(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Changes here are recorded (Windows' own busy areas are skipped unless allowed).</summary>
    private bool InScope(string rel) => !_blocked.Any(b => Under(rel, b)) || _allowed.Any(a => Under(rel, a));

    /// <summary>We walk into a key if it is in scope or leads to an allowed area.</summary>
    private bool ShouldDescend(string rel) =>
        InScope(rel) || _allowed.Any(a => a.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase));

    private string Full(string rel) => rel.Length == 0 ? _displayRoot : $@"{_displayRoot}\{rel}";

    public void Compare(RegistryKey live, RegistryKey before, string rel = "")
    {
        if (++_visited % 5000 == 0) _progress?.Report($"Comparing registry… {_visited:N0} keys");

        if (_compareValues && InScope(rel) && WrittenSinceStart(live)) CompareValues(live, before, rel);

        string[] liveSubs, beforeSubs;
        try { liveSubs = live.GetSubKeyNames(); } catch (Exception) { return; }
        try { beforeSubs = before.GetSubKeyNames(); } catch (Exception) { return; } // unreadable copy ≠ empty
        var beforeSet = new HashSet<string>(beforeSubs, StringComparer.OrdinalIgnoreCase);

        foreach (var name in liveSubs)
        {
            var child = rel.Length == 0 ? name : $@"{rel}\{name}";
            if (!ShouldDescend(child)) continue;
            if (!beforeSet.Contains(name))
            {
                if (InScope(child) && IsReallyNew(live, name)) _onAddedKey(Full(child));
                continue;
            }
            if (_topLevelOnly) continue;
            try
            {
                using var liveChild = live.OpenSubKey(name);
                using var beforeChild = before.OpenSubKey(name);
                if (liveChild is not null && beforeChild is not null) Compare(liveChild, beforeChild, child);
            }
            catch (Exception) { /* no access – skip */ }
        }
    }

    // ------------------------------------------------------------------ snapshot fallback
    public RegSnapshot TakeSnapshot(RegistryKey root)
    {
        var snap = new RegSnapshot();
        Walk(root, "", snap);
        return snap;
    }

    private void Walk(RegistryKey key, string rel, RegSnapshot snap)
    {
        if (++_visited % 5000 == 0) _progress?.Report($"Reading registry… {_visited:N0} keys");
        var values = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
        if (_compareValues && InScope(rel))
        {
            string[]? names;
            try { names = key.GetValueNames(); } catch (Exception) { names = null; }
            if (names is null) values[UnknownMarker] = Unreadable;
            else
                foreach (var name in names)
                    values[name] = RegValue.Read(key, name) ?? Unreadable;
        }
        snap[rel] = values;
        if (_topLevelOnly && rel.Length > 0) return;

        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch (Exception) { return; }
        foreach (var name in subs)
        {
            var child = rel.Length == 0 ? name : $@"{rel}\{name}";
            if (!ShouldDescend(child)) continue;
            try
            {
                using var sub = key.OpenSubKey(name);
                if (sub is not null) Walk(sub, child, snap);
                else snap[child] = UnknownValues(); // exists, but no access
            }
            catch (Exception) { snap[child] = UnknownValues(); }
        }
    }

    public void Compare(RegistryKey live, RegSnapshot before, string rel = "")
    {
        if (++_visited % 5000 == 0) _progress?.Report($"Comparing registry… {_visited:N0} keys");

        if (_compareValues && InScope(rel) && WrittenSinceStart(live) && before.TryGetValue(rel, out var beforeValues))
            CompareValues(live, beforeValues, rel);

        string[] liveSubs;
        try { liveSubs = live.GetSubKeyNames(); } catch (Exception) { return; }
        foreach (var name in liveSubs)
        {
            var child = rel.Length == 0 ? name : $@"{rel}\{name}";
            if (!ShouldDescend(child)) continue;
            if (!before.ContainsKey(child))
            {
                if (InScope(child) && IsReallyNew(live, name)) _onAddedKey(Full(child));
                continue;
            }
            if (_topLevelOnly) continue;
            try
            {
                using var liveChild = live.OpenSubKey(name);
                if (liveChild is not null) Compare(liveChild, before, child);
            }
            catch (Exception) { }
        }
    }

    /// <summary>Marks a key or value that couldn't be read when the snapshot was taken – never compared.</summary>
    private const string UnknownMarker = "\0purgely-unknown";
    private static readonly RegValue Unreadable = new() { Kind = "Unreadable" };
    private static Dictionary<string, RegValue> UnknownValues() =>
        new(StringComparer.OrdinalIgnoreCase) { [UnknownMarker] = Unreadable };

    private void CompareValues(RegistryKey live, Dictionary<string, RegValue> before, string rel)
    {
        if (before.ContainsKey(UnknownMarker)) return;
        string[] liveNames;
        try { liveNames = live.GetValueNames(); } catch (Exception) { return; }
        var liveSet = new HashSet<string>(liveNames, StringComparer.OrdinalIgnoreCase);

        foreach (var name in liveNames)
        {
            var now = RegValue.Read(live, name);
            if (now is null) continue;
            if (!before.TryGetValue(name, out var old))
            {
                _diff.ValueChanges.Add(new RegValueChange { Key = Full(rel), Name = name, Old = null, New = now });
                continue;
            }
            if (old.Kind == "Unreadable") continue; // survives the JSON round trip
            if (!now.SameAs(old))
                _diff.ValueChanges.Add(new RegValueChange { Key = Full(rel), Name = name, Old = old, New = now });
        }
        foreach (var (name, old) in before)
            if (!liveSet.Contains(name) && old.Kind != "Unreadable")
                _diff.DeletedValues.Add(new RegValueChange { Key = Full(rel), Name = name, Old = old });
    }

    private void CompareValues(RegistryKey live, RegistryKey before, string rel)
    {
        string[] liveNames, beforeNames;
        try { liveNames = live.GetValueNames(); } catch (Exception) { return; }
        try { beforeNames = before.GetValueNames(); } catch (Exception) { return; } // unreadable copy ≠ empty
        var beforeSet = new HashSet<string>(beforeNames, StringComparer.OrdinalIgnoreCase);
        var liveSet = new HashSet<string>(liveNames, StringComparer.OrdinalIgnoreCase);

        foreach (var name in liveNames)
        {
            var now = RegValue.Read(live, name);
            if (now is null) continue;
            if (!beforeSet.Contains(name))
            {
                _diff.ValueChanges.Add(new RegValueChange { Key = Full(rel), Name = name, Old = null, New = now });
                continue;
            }
            var old = RegValue.Read(before, name);
            if (old is not null && !now.SameAs(old))
                _diff.ValueChanges.Add(new RegValueChange { Key = Full(rel), Name = name, Old = old, New = now });
        }

        foreach (var name in beforeNames.Where(n => !liveSet.Contains(n)))
            if (RegValue.Read(before, name) is { } old)
                _diff.DeletedValues.Add(new RegValueChange { Key = Full(rel), Name = name, Old = old });
    }
}
