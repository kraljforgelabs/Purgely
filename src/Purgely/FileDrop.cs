using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Purgely;

/// <summary>
/// Lets a file be dropped on a Purgely window – also from Explorer, although Purgely runs as administrator (Windows
/// normally blocks drag and drop from a normal program into an administrator's window). Uses the classic WM_DROPFILES
/// message, which Windows can be told to let through. Only the path arrives; nothing happens without the user's OK.
/// </summary>
public static class FileDrop
{
    private const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049, MSGFLT_ALLOW = 1;

    public static void Enable(Window window, Action<string> onDrop)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (HwndSource.FromHwnd(hwnd) is not { } source) return;
            void Arm()
            {
                try
                {
                    // WM_DROPFILES only arrives when no OLE drop target is registered – and WPF registers one again
                    // for elements that accept drops (a text box), so this is repeated once the window is up.
                    var hr = RevokeDragDrop(hwnd);
                    DragAcceptFiles(hwnd, true);
                    Trace($"{window.GetType().Name}: revoke 0x{hr:X8}");
                }
                catch (Exception) { }
            }
            try
            {
                // What Windows needs to hand a dropped file across to an administrator's window. Only this window's
                // filter changes; Purgely's windows don't act on WM_COPYDATA (the single-instance window keeps its
                // default filter).
                foreach (var msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                {
                    var ok = ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
                    Trace($"{window.GetType().Name}: filter 0x{msg:X4} {(ok ? "ok" : "failed " + Marshal.GetLastWin32Error())}");
                }
            }
            catch (Exception) { return; }
            Arm();
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(Arm), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.ContentRendered += (_, _) => Arm();
            window.Activated += (_, _) => Arm();
            window.MouseEnter += (_, _) => Arm(); // cheap, and covers anything that registered a drop target meanwhile

            source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_DROPFILES) return IntPtr.Zero;
                handled = true;
                Trace($"{window.GetType().Name}: WM_DROPFILES");
                string? path = null;
                try
                {
                    var length = DragQueryFile(wParam, 0, null, 0); // the first item only
                    if (length is > 0 and < 32767)
                    {
                        var buffer = new StringBuilder((int)length + 1);
                        if (DragQueryFile(wParam, 0, buffer, buffer.Capacity) == length) path = buffer.ToString();
                    }
                }
                finally { DragFinish(wParam); }
                if (path is { Length: > 3 })
                    window.Dispatcher.BeginInvoke(() => { window.Activate(); onDrop(path); });
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>Test builds only: a small log next to the .exe, to see what Windows delivered.</summary>
    [System.Diagnostics.Conditional("UITEST"), System.Diagnostics.Conditional("DROPLOG")]
    internal static void Trace(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "drop.log"), $"{DateTime.Now:HH:mm:ss} {line}\n"); }
        catch (Exception) { }
    }

    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, int size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);
}
