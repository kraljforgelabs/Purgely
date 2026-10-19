using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>
/// The PATH environment variable (system: HKLM\...\Session Manager\Environment, user: HKCU\Environment).
/// Entries are read and written without expanding %variables%, and the value keeps its type (REG_EXPAND_SZ),
/// so removing one entry never changes the others.
/// </summary>
public static class PathVariable
{
    public const string SystemKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    public const string UserKey = @"HKCU\Environment";
    public const string ValueName = "Path";

    /// <summary>The raw entries of one PATH value (unexpanded, in order).</summary>
    public static List<string> Read(string keyPath)
    {
        try
        {
            using var key = RegistryPath.Open(keyPath);
            var raw = key?.GetValue(ValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            return raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    public static bool SameEntry(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes every copy of <paramref name="entry"/>; <paramref name="position"/> is where the first copy was (for Undo).
    /// Returns false if PATH has an unusual type and nothing was changed.
    /// </summary>
    public static bool RemoveEntry(string keyPath, string entry, out bool wasThere, out int position)
    {
        wasThere = false;
        position = -1;
        using var key = RegistryPath.Open(keyPath, writable: true);
        if (key is null || !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase)) return true; // no PATH here
        var kind = key.GetValueKind(ValueName);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return false;
        var raw = key.GetValue(ValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = raw.Split(';').ToList();
        position = parts.FindIndex(p => p.Trim().Length > 0 && SameEntry(p, entry));
        var kept = parts.Where(p => !(p.Trim().Length > 0 && SameEntry(p, entry))).ToList();
        wasThere = kept.Count != parts.Count;
        if (!wasThere) return true;
        key.SetValue(ValueName, string.Join(";", kept), kind);
        Broadcast();
        return true;
    }

    /// <summary>Puts an entry back where it was (Undo; at the end if the position is unknown). Nothing happens if it is already there.</summary>
    public static void AddEntry(string keyPath, string entry, int position = -1)
    {
        using var key = RegistryPath.Create(keyPath);
        var exists = key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase);
        var kind = exists ? key.GetValueKind(ValueName) : RegistryValueKind.ExpandString;
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidOperationException("PATH has an unusual type now – add the entry back by hand: " + entry);
        var raw = exists ? key.GetValue(ValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string : null;
        var parts = string.IsNullOrEmpty(raw) ? new List<string>() : raw.Split(';').ToList(); // empty segments stay as they were
        if (parts.Any(p => p.Trim().Length > 0 && SameEntry(p, entry))) return;
        if (position >= 0 && position <= parts.Count) parts.Insert(position, entry);
        else if (parts.Count > 0 && parts[^1].Length == 0) parts[^1] = entry; // "a;b;" → "a;b;entry"
        else parts.Add(entry);
        key.SetValue(ValueName, string.Join(";", parts), kind);
        Broadcast();
    }

    // Tell running programs (Explorer) that the environment changed, so new windows see the new PATH.
    private const int HWND_BROADCAST = 0xFFFF, WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    private static void Broadcast()
    {
        try { SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _); }
        catch (Exception) { }
    }
}
