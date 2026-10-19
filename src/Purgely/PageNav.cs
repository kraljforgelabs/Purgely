using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Purgely;

/// <summary>
/// Makes Purgely behave like one app: a page opens in place of the window it was opened from (same position,
/// size and state), that window hides, and closing the page – Home/Back button, Esc or X – brings it back
/// where the page was.
/// </summary>
public static class PageNav
{
    /// <summary>Purgely is closing on purpose (e.g. after "Remove Purgely") – pages must not bring other windows back.</summary>
    public static bool Exiting { get; set; }

    public static void Open(Window from, Window page, Action? onBack = null)
    {
        var bounds = from.WindowState == WindowState.Normal ? new Rect(from.Left, from.Top, from.Width, from.Height) : from.RestoreBounds;
        page.Owner = null;
        page.WindowStartupLocation = WindowStartupLocation.Manual;
        if (!bounds.IsEmpty)
        {
            page.Left = bounds.Left;
            page.Top = bounds.Top;
            page.Width = Math.Max(bounds.Width, page.MinWidth);
            page.Height = Math.Max(bounds.Height, page.MinHeight);
        }
        page.WindowState = from.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        page.ShowInTaskbar = true;

        // Pages show a Home button (named HomeButton); opened from another page it reads "Back".
        if (page.FindName("HomeButton") is Button home)
        {
            home.Visibility = Visibility.Visible;
            if (from is not HomeWindow)
            {
                home.ToolTip = "Back (Esc)";
                foreach (var text in FindTexts(home)) if (text.Text == "Home") text.Text = "Back";
            }
            home.Click += (_, _) => page.Close();
        }
        page.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (Keyboard.FocusedElement is TextBox { Text.Length: > 0 }) return; // Esc clears the search box first
            page.Close();
            e.Handled = true;
        };

        // The window's own X (title bar, taskbar thumbnail, Alt+F4) means "close Purgely", like any Windows app.
        // Home/Back and Esc mean "go back". Windows sends SC_CLOSE only for the first kind.
        var exitRequested = false;
        page.SourceInitialized += (_, _) =>
        {
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(page).Handle) is { } source)
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    const int WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;
                    if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_CLOSE) exitRequested = true;
                    return IntPtr.Zero;
                });
        };
        page.Closing += (_, e) =>
        {
            if (e.Cancel) { exitRequested = false; return; } // BusyGuard said "wait" – nothing closes
            if (exitRequested || Exiting) return;
            // Show the previous window BEFORE this one disappears: if it were shown afterwards, Windows would
            // already have given the focus to some other program's window.
            var back = page.WindowState == WindowState.Normal
                ? new Rect(page.Left, page.Top, page.Width, page.Height) : page.RestoreBounds;
            if (!back.IsEmpty)
            {
                from.Left = back.Left; from.Top = back.Top; from.Width = back.Width; from.Height = back.Height;
            }
            from.WindowState = page.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            from.Show();
            from.Activate();
        };

        page.Closed += (_, _) =>
        {
            if (Exiting) return;
            if (exitRequested)
            {
                Application.Current.Shutdown();
                return;
            }
            from.Activate(); // once more, in case the closing page still held the focus
            onBack?.Invoke();
        };

        page.Show();
        from.Hide();
    }

    private static IEnumerable<TextBlock> FindTexts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock t) yield return t;
            foreach (var inner in FindTexts(child)) yield return inner;
        }
    }
}
