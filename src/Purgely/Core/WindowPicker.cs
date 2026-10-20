using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Purgely.Core;

/// <summary>Which program owns the window under the mouse pointer ("drag the target onto a window"). Read-only.</summary>
public static class WindowPicker
{
    /// <summary>
    /// The .exe of the window under the pointer; null when Windows won't say. <paramref name="own"/> is true when the
    /// pointer is over one of Purgely's own windows.
    /// </summary>
    public static string? ProgramUnderPointer(out bool own)
    {
        own = false;
        if (!GetCursorPos(out var point)) return null;
        var hwnd = WindowFromPoint(point);
        if (hwnd == IntPtr.Zero) return null;
        var root = GetAncestor(hwnd, 2); // GA_ROOT
        if (root != IntPtr.Zero) hwnd = root;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) own = true;
        if (pid == 0 || own) return null;
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// The desktop icon or File Explorer item under the pointer, as a file path – so "Find by window" also works when it
    /// is dropped on an icon. Null when the pointer isn't over an item (or Windows won't say).
    /// </summary>
    public static string? ShellItemUnderPointer()
    {
        try
        {
            if (!GetCursorPos(out var point)) return null;
            var root = GetAncestor(WindowFromPoint(point), 2);
            if (root == IntPtr.Zero) return null;
            var cls = new StringBuilder(256);
            GetClassName(root, cls, cls.Capacity);
            FileDrop.Trace($"picker: root class {cls}, point {point.X},{point.Y}");

            var element = ItemAt(point);
            FileDrop.Trace($"picker: item '{element?.Current.Name}'");
            if (element is null) return null;
            var name = element.Current.Name;
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            IEnumerable<string> folders = cls.ToString() is "Progman" or "WorkerW"
                ? new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                }
                : ExplorerFolder(root) is { } shown ? new[] { shown } : Array.Empty<string>();
            foreach (var folder in folders.Where(f => f.Length > 3 && Directory.Exists(f)))
            {
                FileDrop.Trace($"picker: looking in {folder}");
                // Explorer hides known extensions: "Steam" may be Steam.lnk or Steam.url.
                var exact = Path.Combine(folder, name);
                if (File.Exists(exact) || Directory.Exists(exact)) return exact;
                var match = Directory.EnumerateFiles(folder, name + ".*", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => Path.GetExtension(f).ToLowerInvariant() switch { ".lnk" => 0, ".url" => 1, ".exe" => 2, _ => 3 })
                    .FirstOrDefault();
                if (match is not null) return match;
            }
        }
        catch (Exception ex) { FileDrop.Trace("picker: " + ex); }
        return null;
    }

    /// <summary>
    /// The list item (desktop icon, file) under the point. Asks Windows for the element at the point first; when that
    /// doesn't answer (it often doesn't for an administrator's program), looks through the items of the list under
    /// the pointer for the one whose rectangle holds the point.
    /// </summary>
    private static AutomationElement? ItemAt(POINT point)
    {
        var screen = new System.Windows.Point(point.X, point.Y);
        try
        {
            var element = AutomationElement.FromPoint(screen);
            FileDrop.Trace($"picker: at point {element?.Current.ControlType.ProgrammaticName} '{element?.Current.Name}'");
            for (var i = 0; element is not null && i < 4; i++)
            {
                if (element.Current.ControlType == ControlType.ListItem) return element;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception ex) { FileDrop.Trace("picker: FromPoint " + ex.GetType().Name); }

        try
        {
            var list = WindowFromPoint(point);
            if (list == IntPtr.Zero) return null;
            var host = AutomationElement.FromHandle(list);
            var items = host.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            FileDrop.Trace($"picker: {items.Count} item(s) in the window under the pointer");
            foreach (AutomationElement item in items)
                if (item.Current.BoundingRectangle.Contains(screen)) return item;
        }
        catch (Exception ex) { FileDrop.Trace("picker: FromHandle " + ex.GetType().Name); }
        return null;
    }

    /// <summary>The folder a File Explorer window shows (through the Shell's list of open windows).</summary>
    private static string? ExplorerFolder(IntPtr window)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)w.HWND) != window) continue;
                    string path = w.Document.Folder.Self.Path;
                    return Path.IsPathFullyQualified(path) ? path : null;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
