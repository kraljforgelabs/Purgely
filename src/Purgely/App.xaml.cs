using System.Windows;
using System.Windows.Threading;

namespace Purgely;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "Purgely – error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
