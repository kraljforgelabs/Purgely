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

    /// <summary>
    /// Errors before any window exists (or on a background thread) would otherwise close Purgely without a word. They
    /// are written to %LOCALAPPDATA%\Purgely\crash.log and shown, so "nothing happens" never stays a mystery.
    /// </summary>
    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
    }

    internal static void ReportCrash(Exception? ex)
    {
        var log = "";
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely");
            System.IO.Directory.CreateDirectory(folder);
            log = System.IO.Path.Combine(folder, "crash.log");
            StartupLog.Step("crashed: " + ex?.GetType().Name);
            System.IO.File.AppendAllText(log,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Purgely {typeof(App).Assembly.GetName().Version?.ToString(3)}  " +
                $"Windows {Environment.OSVersion.Version}  {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\n{ex}\n\n");
        }
        catch (Exception) { }
        try
        {
            MessageBox.Show(
                $"Purgely couldn't start:\n\n{ex?.Message}\n\n" +
                (log.Length > 0 ? $"The details are in {log}. " : "") +
                "Please report it at github.com/kraljforgelabs/Purgely/issues – with that file it can be fixed quickly.",
                "Purgely – error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        StartTarget = TargetFrom(e.Args);
        // One Purgely at a time: two copies cleaning or restoring at once could get in each other's way.
        _instance = new Mutex(true, @"Local\KraljForgeLabs.Purgely", out var first);
        StartupLog.Step("started" + (StartTarget is null ? "" : " with an item") + (first ? "" : " – another Purgely is running"));
        if (!first)
        {
            // Hand the item (or just "come to the front") to the Purgely that is already open.
            var answer = SingleInstance.Send(StartTarget ?? "");
            StartupLog.Step("handed over to the running Purgely: " + answer);
            if (answer != SingleInstance.Answer.Visible)
                MessageBox.Show(answer == SingleInstance.Answer.NoWindow
                        ? "Purgely is already running, but its window isn't open yet – it may still be starting, or it got stuck.\n\n" +
                          "Wait a few seconds. If no window appears, open Task Manager (Ctrl+Shift+Esc) → Details, end \"Purgely.exe\" " +
                          "and start Purgely again."
                        : "Purgely is already running but isn't answering.\n\n" +
                          "Open Task Manager (Ctrl+Shift+Esc) → Details, end \"Purgely.exe\" and start Purgely again.",
                    "Purgely", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }
        // A small "Starting…" window until the home screen is up (fades out on its own).
        try { new SplashScreen("splash.png").Show(autoClose: true, topMost: false); } catch (Exception) { }
        base.OnStartup(e);
        SingleInstance.Listen(OnItemFromAnotherCopy);
        StartupLog.Step("ready, opening the home screen");
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
            catch (Exception ex) { StartupLog.Step("housekeeping failed: " + ex.GetType().Name); }
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
