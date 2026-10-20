using System.ComponentModel;
using System.Windows;
using Purgely.Core;
using Purgely.Core.Startup;

namespace Purgely;

public enum CheckupKind { Junk, DeadLeftovers, Startup, Clutter, Bloatware, Updates, Drivers }

/// <summary>One finding of the checkup.</summary>
public sealed class CheckupSection : INotifyPropertyChanged
{
    public required CheckupKind Kind { get; init; }
    public required string Icon { get; init; }
    public required string Title { get; set; }
    public required string Summary { get; set; }
    public bool CanFix { get; set; }
    public string OpenText { get; init; } = "Review";
    private bool _checked;
    public bool IsChecked { get => _checked; set { _checked = value; Changed(nameof(IsChecked)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    internal object? Payload { get; init; }
    /// <summary>What exactly would be fixed – shown under the card with "Details".</summary>
    public List<string> Details { get; init; } = new();
    public bool HasDetails => Details.Count > 0;
    private bool _failed;
    /// <summary>Something could not be fixed – the status is shown in the attention colour.</summary>
    public bool Failed { get => _failed; set { _failed = value; Changed(nameof(Failed)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>
/// One-click checkup: looks at installer junk, dead leftovers, startup, ads/privacy, bloatware, updates and drivers.
/// Only knowledge-base-safe or proven-dead items are ticked; every fix goes through the same code (and Undo) as the
/// feature's own page.
/// </summary>
public partial class CheckupWindow : Window
{
    private List<CheckupSection> _sections = new();
    private List<InstalledProgram> _programs = new();

    public CheckupWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        BusyOverlay.Visibility = Visibility.Visible;
        var sections = new List<CheckupSection>();
        try
        {
            BusyTitle.Text = "Reading your programs…";
            _programs = await Task.Run(ProgramInventory.LoadWithRecentlyRemoved);
            var all = _programs;

            BusyTitle.Text = "Looking for installer junk…";
            var junk = await Task.Run(() => InstallerJunkScanner.Scan(all).Where(i => i.Recommended).ToList());
            sections.Add(new CheckupSection
            {
                OpenText = "Open page", Kind = CheckupKind.Junk, Icon = "", Title = "Installer junk",
                Summary = junk.Count == 0 ? "Nothing to clean." : $"{junk.Count} old setup file(s) no program needs – {Sizes.Format(junk.Sum(i => i.SizeBytes))}.",
                CanFix = junk.Count > 0, IsChecked = junk.Count > 0, Payload = junk,
                Details = junk.Select(i => $"{i.DisplayLocation}  ({i.SizeText})").ToList(),
            });

            BusyTitle.Text = "Sweeping for dead leftovers…";
            var dead = await Task.Run(() => OrphanSweep.Scan(all).Where(i => i.Recommended).ToList());
            sections.Add(new CheckupSection
            {
                OpenText = "Open page", Kind = CheckupKind.DeadLeftovers, Icon = "", Title = "Dead leftovers",
                Summary = dead.Count == 0 ? "No shortcuts, tasks or startup entries pointing to deleted programs."
                    : $"{dead.Count} item(s) that point to programs which no longer exist (shortcuts, tasks, startup entries, empty folders).",
                CanFix = dead.Count > 0, IsChecked = dead.Count > 0, Payload = dead,
                Details = dead.Select(i => $"{i.KindText}: {i.DisplayLocation}").ToList(),
            });

            BusyTitle.Text = "Checking startup…";
            var startup = await Task.Run(() => StartupManager.OptimizeCandidates(StartupScanner.Scan(all)));
            sections.Add(new CheckupSection
            {
                OpenText = "Open page", Kind = CheckupKind.Startup, Icon = "", Title = "Startup",
                Summary = startup.Count == 0 ? "Nothing that is known to be safe to turn off."
                    : $"{startup.Count} startup program(s) known to be safe to turn off: {string.Join(", ", startup.Take(4).Select(s => s.Name))}{(startup.Count > 4 ? "…" : "")}",
                CanFix = startup.Count > 0, IsChecked = startup.Count > 0, Payload = startup,
                Details = startup.Select(i => $"{i.Name} – {i.Program}").ToList(),
            });

            BusyTitle.Text = "Checking ads and privacy settings…";
            var tweaks = await Task.Run(() => Bloatware.ReadTweaks().Where(t => t.IsOn && t.TickByDefault).ToList());
            sections.Add(new CheckupSection
            {
                OpenText = "Open page", Kind = CheckupKind.Clutter, Icon = "", Title = "Ads, AI & privacy",
                Summary = tweaks.Count == 0 ? "All ads, suggestions and optional data collection are off."
                    : $"{tweaks.Count} on: {string.Join(", ", tweaks.Take(4).Select(t => t.Name))}{(tweaks.Count > 4 ? "…" : "")}",
                CanFix = tweaks.Count > 0, IsChecked = tweaks.Count > 0, Payload = tweaks,
                Details = tweaks.Select(t => $"{t.Name} – {t.What}").ToList(),
            });

            var bloat = Bloatware.Scan(all).Where(b => b.IsChecked && b.Program.IsStoreApp).ToList();
            sections.Add(new CheckupSection
            {
                OpenText = "Open page", Kind = CheckupKind.Bloatware, Icon = "", Title = "Bloatware",
                Summary = bloat.Count == 0 ? "No ad or promotion apps found."
                    : $"{bloat.Count} app(s) with ads or promotions: {string.Join(", ", bloat.Take(4).Select(b => b.Name))}{(bloat.Count > 4 ? "…" : "")}",
                CanFix = bloat.Count > 0, IsChecked = bloat.Count > 0, Payload = bloat,
                Details = bloat.Select(b => $"{b.Name} – {b.Why}").ToList(),
            });

            BusyTitle.Text = "Asking winget for updates…";
            var updates = await Task.Run(WingetUpdates.Check);
            var count = updates is null ? -1 : WingetUpdates.Attach(all, updates);
            sections.Add(new CheckupSection
            {
                Kind = CheckupKind.Updates, Icon = "", Title = "Program updates", OpenText = "Open Updates",
                Summary = count < 0 ? "winget (App Installer) is not available." : count == 0 ? "Everything is up to date."
                    : $"{count} program(s) have a newer version. Updates are installed one by one on the Program updates page.",
            });
            sections.Add(new CheckupSection
            {
                Kind = CheckupKind.Drivers, Icon = "", Title = "Drivers", OpenText = "Open Drivers",
                Summary = "Driver checks download catalog data, so they run on the Driver updates page. Only signed drivers from Microsoft or your PC's maker are offered.",
            });
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "The checkup could not finish.\n\n" + ex.Message, "Checkup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; }

        _sections = sections;
        SectionsList.ItemsSource = _sections;
        foreach (var s in _sections) s.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var fixable = _sections.Count(s => s.CanFix);
        var ticked = _sections.Count(s => s.CanFix && s.IsChecked);
        FixButton.IsEnabled = ticked > 0;
        var updates = _sections.FirstOrDefault(s => s.Kind == CheckupKind.Updates)?.Summary ?? "";
        var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        SummaryText.Text = (fixable == 0 ? "Nothing to fix here." : $"{fixable} thing(s) to fix · {ticked} ticked") +
                           (updates.Contains("newer version") ? "  ·  program updates are waiting (see below)" : "") +
                           (admin ? "" : "  ·  not running as administrator – some things can't be checked");
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _sections.Where(s => s.CanFix && s.IsChecked).ToList();
        if (Dialog.Show(this, $"Fix {chosen.Count} thing(s)?\n\n" + string.Join("\n", chosen.Select(s => $"• {s.Title}: {s.Summary}")) +
                              "\n\nEverything can be undone in the Undo center (startup: \"Undo optimize\" on the Startup page; " +
                              "Store apps: reinstall from the Microsoft Store).",
                "Checkup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            foreach (var s in chosen)
            {
                BusyTitle.Text = $"Fixing: {s.Title}…";
                try
                {
                    // FixOne runs off the UI thread and returns its result; the card is updated here, on the UI thread.
                    var (status, failed) = await Task.Run(() => FixOne(s));
                    s.Status = status;
                    s.Failed = failed;
                }
                catch (Exception ex) { s.Status = "Failed: " + ex.Message; s.Failed = true; }
                s.CanFix = false;
                s.IsChecked = false;
            }
        }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; }
        SectionsList.Items.Refresh();
        UpdateSummary();
    }

    private static (string Status, bool Failed) FixOne(CheckupSection s)
    {
        switch (s.Kind)
        {
            case CheckupKind.Junk or CheckupKind.DeadLeftovers when s.Payload is List<LeftoverItem> items:
                var report = Cleaner.Clean(s.Kind == CheckupKind.Junk ? "Checkup – installer junk" : "Checkup – dead leftovers", items);
                return ($"✓ Removed {report.Removed}" + (report.Failures.Count > 0 ? $", {report.Failures.Count} could not be removed" : "") +
                        (report.ScheduledForReboot > 0 ? " (some after a restart)" : "") + " – saved to the Undo center.", report.Failures.Count > 0);
            case CheckupKind.Startup when s.Payload is List<StartupItem> startup:
                var off = StartupManager.Optimize(startup);
                return ($"✓ Turned off {off} – \"Undo optimize\" on the Startup page turns them back on.", off < startup.Count);
            case CheckupKind.Clutter when s.Payload is List<ClutterTweak> tweaks:
                var results = Bloatware.TurnOff(tweaks);
                var refused = results.Where(r => !r.Ok).Select(r => $"{r.Tweak.Name}: {r.Message}").ToList();
                return ($"✓ Turned off {results.Count(r => r.Ok)} of {results.Count} – restore \"Windows clutter\" in the Undo center to undo." +
                        (refused.Count > 0 ? " Not possible: " + string.Join("; ", refused) : ""), refused.Count > 0);
            case CheckupKind.Bloatware when s.Payload is List<BloatItem> apps:
                var removed = apps.Count(a => StoreApps.Remove(a.Program).Ok);
                return ($"✓ Removed {removed} of {apps.Count} app(s) – reinstall any of them from the Microsoft Store.", removed < apps.Count);
            default:
                return ("", false);
        }
    }

    private void OpenSection_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CheckupSection s) return;
        Window page = s.Kind switch
        {
            CheckupKind.Junk => new ScanResultsWindow("Installer junk", (List<LeftoverItem>)s.Payload!, null, null, heading: "Hidden installer junk"),
            CheckupKind.DeadLeftovers => new ScanResultsWindow("Dead leftovers", (List<LeftoverItem>)s.Payload!, null, null, heading: "Orphaned leftovers"),
            CheckupKind.Startup => new StartupWindow(_programs),
            CheckupKind.Clutter => new BloatwareWindow(startTab: 1),
            CheckupKind.Bloatware => new BloatwareWindow(startTab: 0),
            CheckupKind.Updates => new MainWindow(MainView.Updates),
            _ => new HardwareWindow(),
        };
        PageNav.Open(this, page, () => _ = RunAsync());
    }
}
