using System.ComponentModel;
using System.Windows;
using Purgely.Core;

namespace Purgely;

/// <summary>Stops a window from closing while Purgely is cleaning or restoring (Esc, Home, X or owner close).</summary>
public static class BusyGuard
{
    public static void Attach(Window window) => window.Closing += (_, e) => Check(window, e);

    private static void Check(Window window, CancelEventArgs e)
    {
        if (e.Cancel || !Activity.IsBusy) return;
        e.Cancel = true;
        Dialog.Show(window, "Purgely is changing your PC right now (removing, restoring or installing).\n\n" +
                                "Please wait until it has finished – closing now could leave an Undo package that can't be restored.",
            "Please wait", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
