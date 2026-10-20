using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Purgely;

/// <summary>
/// Hands a right-clicked or dropped item to the Purgely that is already open, instead of starting a second copy.
/// The open Purgely has a hidden message-only window; a new copy sends it the path (WM_COPYDATA) and exits.
/// Windows only delivers it from programs with the same or higher rights, and the path is checked again on arrival.
/// </summary>
public static class SingleInstance
{
    private const string WindowName = "KraljForgeLabs.Purgely.Messages";
    private const int WM_COPYDATA = 0x004A;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr Magic = new(0x50555247); // "PURG"
    private static HwndSource? _source;

    /// <summary>The open Purgely: listen for items from new copies (an empty path means "come to the front").</summary>
    public static void Listen(Action<string> onItem)
    {
        if (_source is not null) return;
        try
        {
            _source = new HwndSource(new HwndSourceParameters(WindowName) { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
            _source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_COPYDATA) return IntPtr.Zero;
                handled = true;
                var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (data.dwData != Magic || data.cbData < 0 || data.cbData > 32767 * 2 || data.cbData % 2 != 0) return IntPtr.Zero;
                var path = data.cbData == 0 ? "" : Marshal.PtrToStringUni(data.lpData, data.cbData / 2) ?? "";
                _source.Dispatcher.BeginInvoke(() => onItem(path)); // don't keep the sender waiting
                return new IntPtr(1);
            });
        }
        catch (Exception) { _source = null; }
    }

    /// <summary>A new copy: give the item to the open Purgely. False when there is none that answers.</summary>
    public static bool Send(string path)
    {
        try
        {
            var hwnd = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, WindowName);
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            AllowSetForegroundWindow(pid); // so the open Purgely may come to the front
            var text = Marshal.StringToHGlobalUni(path);
            try
            {
                var data = new COPYDATASTRUCT { dwData = Magic, cbData = path.Length * 2, lpData = text };
                return SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref data, 0x0002, 5000, out var result) != IntPtr.Zero &&
                       result == new IntPtr(1);
            }
            finally { Marshal.FreeHGlobal(text); }
        }
        catch (Exception) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);
}
