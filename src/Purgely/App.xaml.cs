using System.Windows;
using System.Windows.Threading;

namespace Purgely;

public partial class App : Application
{
    /// <summary>
    /// The item Purgely was started with: "Purgely.exe --uninstall &lt;path&gt;" (right-click menu) or a file dropped on
    /// Purgely.exe. Only an existing local file or folder; the home screen opens the uninstall for it.
    /// </summary>
    public static string? StartTarget { get; set; }

    private static Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        StartTarget = TargetFrom(e.Args);
        // One Purgely at a time: two copies cleaning or restoring at once could get in each other's way.
        _instance = new Mutex(true, @"Local\KraljForgeLabs.Purgely", out var first);
        if (!first)
        {
            // Hand the item (or just "come to the front") to the Purgely that is already open.
            if (!SingleInstance.Send(StartTarget ?? ""))
                MessageBox.Show(StartTarget is null
                        ? "Purgely is already open."
                        : "Purgely is already open but isn't answering. Close it and try again.",
                    "Purgely", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }
        base.OnStartup(e);
        SingleInstance.Listen(OnItemFromAnotherCopy);
        // Housekeeping in the background: keep Purgely's data folders administrators-only, apply the Timeline setting.
        _ = Task.Run(() =>
        {
            try
            {
                Core.DataFolders.Protect();
                Core.ExplorerMenu.FollowMove(Environment.ProcessPath); // the right-click entry follows a moved Purgely.exe
                var keep = Core.AppSettings.Current.TimelineKeepDays;
                if (keep is 30 or 90 or 365) Core.Timeline.ClearHistory(keep); // only the choices offered in Settings
            }
            catch (Exception) { }
        });
    }

    /// <summary>A right-clicked item (or "come to the front") sent by a second copy of Purgely.</summary>
    private void OnItemFromAnotherCopy(string path)
    {
        var visible = Windows.OfType<Window>().Where(w => w.IsVisible && w is not DialogWindow).ToList();
        var front = visible.FirstOrDefault(w => w.IsActive) ?? visible.LastOrDefault();
        if (front is not null)
        {
            if (front.WindowState == WindowState.Minimized) front.WindowState = WindowState.Normal;
            front.Activate();
        }
        if (path.Length == 0 || TargetFrom(new[] { "--uninstall", path }) is not { } target) return;
        // Busy (cleaning, a question open): the user finishes that first – nothing starts on top of it.
        if (Core.Activity.IsBusy || System.Windows.Interop.ComponentDispatcher.IsThreadModal) return;
        if (visible.OfType<MainWindow>().FirstOrDefault() is { } list) list.UninstallItem(target);
        else if (Windows.OfType<HomeWindow>().FirstOrDefault() is { } home) home.UninstallItem(target);
    }

    internal static string? TargetFrom(string[] args)
    {
        var path = args.Length switch
        {
            >= 2 when args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase) => args[1],
            1 when !args[0].StartsWith('-') => args[0],
            _ => null,
        };
        if (path is null || path.Length <= 3 || path.StartsWith(@"\\") || !System.IO.Path.IsPathFullyQualified(path)) return null;
        return System.IO.File.Exists(path) || System.IO.Directory.Exists(path) ? path : null;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "Purgely – error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
