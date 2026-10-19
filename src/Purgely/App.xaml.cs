using System.Windows;
using System.Windows.Threading;

namespace Purgely;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
        // Housekeeping in the background: keep Purgely's data folders administrators-only, apply the Timeline setting.
        _ = Task.Run(() =>
        {
            try
            {
                Core.DataFolders.Protect();
                var keep = Core.AppSettings.Current.TimelineKeepDays;
                if (keep is 30 or 90 or 365) Core.Timeline.ClearHistory(keep); // only the choices offered in Settings
            }
            catch (Exception) { }
        });
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "Purgely – error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
