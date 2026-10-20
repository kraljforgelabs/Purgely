using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Purgely.Core;
using Purgely.Core.Startup;
using Purgely.Core.Trial;

namespace Purgely;

/// <summary>What a notice's action button does.</summary>
public enum NoticeKind { Bundleware, NewStartupItems, KeptOff, ShouldBeOn, ClutterBack, Info, UpdateAvailable, DifferentUser }

/// <summary>
/// One calm alert in the Notices panel. Icon is a Segoe Fluent Icons glyph.
/// Payload carries what the action needs (e.g. the startup items to turn off again).
/// </summary>
public sealed record Notice(string Icon, string Text, string ActionText, NoticeKind Kind, bool IsWarning = false,
                            object? Payload = null);

/// <summary>Visual state of a tile, stored in Button.Tag (the XAML triggers read it).</summary>
public enum TileState { Loading, Ready, Attention }

/// <summary>
/// The start screen: one tile per feature with a live summary, and a Notices panel instead of pop-ups.
/// Nothing heavy runs until it is needed, and nothing here changes the PC by itself.
/// </summary>
public partial class HomeWindow : Window
{
    private const string Warning = "", Info = "";

    public ObservableCollection<Notice> Notices { get; } = new();

    private List<InstalledProgram>? _programs;
    private List<LeftoverItem>? _junk;
    private int _generation;
    private int _steps;
    private static bool _backgroundChecksDone;
    private static bool _bloatChecked; // "Windows brought it back" notice: once per Purgely session
    private static bool _sessionChecked; // update check and "different account" notice: once per Purgely session

    public HomeWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        NoticesList.ItemsSource = Notices;
        Notices.CollectionChanged += (_, _) => UpdateNoticesHeader();
        AdminBadge.Text = IsAdministrator() ? "Administrator" : "Not running as administrator – some things can't be cleaned";
        VersionText.Text = $"KraljForge Labs · free · no ads · v{typeof(HomeWindow).Assembly.GetName().Version?.ToString(3)}";

        Loaded += async (_, _) =>
        {
            TileUninstall.Focus();
            if (!AppSettings.Current.WelcomeSeen) ShowWelcome();
            _ = SessionChecksAsync();
            await RefreshAsync();
        };
    }

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void SetTile(Button tile, TextBlock summary, string text, TileState state)
    {
        summary.Text = text;
        tile.Tag = state.ToString();
    }

    private void UpdateNoticesHeader()
    {
        NoticesCount.Text = Notices.Count > 0 ? Notices.Count.ToString() : "";
        NoticesEmpty.Visibility = Notices.Count == 0 && NoticesBusy.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== summaries
    /// <summary>Recounts every tile, cheapest first. A newer refresh makes older results ignored.</summary>
    private async Task RefreshAsync()
    {
        var gen = ++_generation;
        bool Current() => gen == _generation && IsLoaded;
        _steps = 0;
        FooterProgress.Value = 0;
        FooterStatus.Text = "Getting a quick overview…";
        foreach (var (tile, summary) in Tiles()) SetTile(tile, summary, "Counting…", TileState.Loading);

        // 1. Stores on disk – milliseconds.
        await Step(gen, "Reading history…", async () =>
        {
            var (undo, trials, week) = await Task.Run(() =>
            {
                var u = UndoStore.List().Count(e => !e.Manifest.Restored);
                var t = TrialEngine.List().Count(r => r.State is TrialState.Installing or TrialState.Analyzed);
                var w = Timeline.All().Count(e => e.Time >= DateTime.Now.AddDays(-7));
                return (u, t, w);
            });
            if (!Current()) return;
            SetTile(TileUndo, TileUndoSummary, undo == 0 ? "Nothing to undo" : $"{undo} cleanup(s) can be undone", TileState.Ready);
            SetTile(TileTrial, TileTrialSummary, trials == 0 ? "No program on trial" : $"{trials} program(s) on trial",
                trials > 0 ? TileState.Attention : TileState.Ready);
            SetTile(TileTimeline, TileTimelineSummary, week == 0 ? "Quiet week" : $"{week} event(s) this week", TileState.Ready);
            SetTile(TileDrivers, TileDriversSummary, "Check for newer drivers", TileState.Ready);
        });

        // 2. Installed programs.
        await Step(gen, "Reading installed programs…", async () =>
        {
            var programs = await Task.Run(ProgramInventory.LoadWithRecentlyRemoved);
            if (!Current()) return;
            _programs = programs;
            var orphans = programs.Count(p => p.IsOrphan);
            SetTile(TileUninstall, TileUninstallSummary, $"{programs.Count(p => !p.IsOrphan && !p.IsRemovedEarlier)} programs", TileState.Ready);
            SetTile(TileLeftovers, TileLeftoversSummary,
                orphans == 0 ? "No removed program left traces" : $"{orphans} removed program(s) to check",
                orphans > 0 ? TileState.Attention : TileState.Ready);
        });
        if (_programs is null || !Current()) return;
        var all = _programs;

        // 2b. Bloatware and clutter – reads the list above and a few registry values, so it is quick.
        await Step(gen, "Looking for bloatware…", async () =>
        {
            var firstTime = !_bloatChecked;
            _bloatChecked = true;
            var (bloat, clutterOn, back) = await Task.Run(() =>
            {
                var b = Bloatware.Scan(all).Count(i => i.Rating == BloatRating.Remove);
                var c = Bloatware.ReadTweaks().Count(t => t.IsOn && t.TickByDefault);
                return (b, c, firstTime ? Bloatware.Returned(all) : (new List<ClutterTweak>(), new List<InstalledProgram>()));
            });
            if (back.Item1.Count + back.Item2.Count > 0)
                Notices.Add(new Notice(Warning,
                    "Windows brought back what you removed or turned off: " +
                    $"{Names(back.Item1.Select(t => t.Name).Concat(back.Item2.Select(a => a.Name)))}. " +
                    "This often happens after a big Windows update. If you brought it back yourself, just dismiss this.",
                    "Review", NoticeKind.ClutterBack, IsWarning: true, Payload: back));
            if (!Current()) return;
            var parts = new List<string>();
            if (bloat > 0) parts.Add($"{bloat} app(s) to remove");
            if (clutterOn > 0) parts.Add($"{clutterOn} ad/privacy setting(s) on");
            SetTile(TileBloatware, TileBloatwareSummary, parts.Count == 0 ? "Clean – no ads or bloatware" : string.Join(" · ", parts),
                parts.Count > 0 ? TileState.Attention : TileState.Ready);
        });

        // 3. Startup and the once-per-session background checks (they become Notices, never pop-ups).
        await Step(gen, "Checking startup…", async () =>
        {
            var firstTime = !_backgroundChecksDone;
            _backgroundChecksDone = true;
            if (firstTime) NoticesBusy.Visibility = Visibility.Visible;
            UpdateNoticesHeader();
            (List<StartupItem> items, BootRecord? boot, List<StartupItem> reEnabled, List<StartupItem> added,
             List<List<InstalledProgram>> bundles, List<StartupItem> shouldBeOn) result;
            try
            {
            result = await Task.Run(() =>
            {
                var items = StartupScanner.Scan(all);
                var boot = BootTimes.Recent(1).FirstOrDefault();
                List<StartupItem> reEnabled = new(), added = new(), shouldBeOn = new();
                List<List<InstalledProgram>> bundles = new();
                if (firstTime)
                {
                    reEnabled = StartupManager.FindReEnabled(items);
                    // Turned off earlier (e.g. by an older Optimize), but Purgely now knows they should stay on.
                    shouldBeOn = items.Where(i => !i.Enabled && i.KeepDisabled && i.Safety == StartupSafety.Keep).ToList();
                    added = StartupManager.FindNew(items);
                }
                // Timeline.Sync ran with every program-list load; it queues bundleware groups for the home screen.
                lock (Timeline.PendingBundles) { bundles = Timeline.PendingBundles.ToList(); Timeline.PendingBundles.Clear(); }
                return (items, boot, reEnabled, added, bundles, shouldBeOn);
            });
            }
            finally
            {
                NoticesBusy.Visibility = Visibility.Collapsed;
                UpdateNoticesHeader();
            }

            // Notices are added even if a newer refresh started meanwhile – they are found only once per session.

            if (result.reEnabled.Count > 0)
                Notices.Add(new Notice(Warning,
                    $"{Names(result.reEnabled.Select(i => i.Name))} {(result.reEnabled.Count == 1 ? "is" : "are")} on at startup again, " +
                    "although you chose \"Keep off\". If you turned it on yourself, just dismiss this.",
                    "Turn off again", NoticeKind.KeptOff, IsWarning: true, Payload: result.reEnabled));
            if (result.shouldBeOn.Count > 0)
                Notices.Add(new Notice(Warning,
                    $"{Names(result.shouldBeOn.Select(i => i.Name))} {(result.shouldBeOn.Count == 1 ? "is" : "are")} off at startup, " +
                    "but should stay on (security, hardware or backup software). Probably turned off by Optimize in an older version of Purgely.",
                    "Turn back on", NoticeKind.ShouldBeOn, IsWarning: true, Payload: result.shouldBeOn));
            if (result.added.Count > 0)
                Notices.Add(new Notice(Info,
                    $"New at Windows startup: {Names(result.added.Select(i => $"{i.Name} ({i.ImpactText.ToLowerInvariant()} impact)"))}.",
                    "Review", NoticeKind.NewStartupItems, Payload: result.added));
            foreach (var group in result.bundles)
                Notices.Add(new Notice(Warning,
                    $"Installed together by different publishers: {Names(group.Select(p => p.Name))}. " +
                    "If you didn't choose all of them, an installer may have slipped in extra software.",
                    "Show in Timeline", NoticeKind.Bundleware, IsWarning: true, Payload: group));

            if (!Current()) return;
            var on = result.items.Count(i => i.Enabled && !i.Delayed);
            var heavy = result.items.Count(i => i.Enabled && !i.Delayed && i.Impact == StartupImpact.High);
            var text = $"{on} start with Windows" + (result.boot is { } b ? $" · last boot {b.Seconds:0} s" : "");
            SetTile(TileStartup, TileStartupSummary, text, heavy >= 3 ? TileState.Attention : TileState.Ready);
        });

        // 4. Installer junk.
        await Step(gen, "Looking for installer junk…", async () =>
        {
            var junk = await Task.Run(() => InstallerJunkScanner.Scan(all));
            if (!Current()) return;
            _junk = junk;
            var bytes = junk.Where(i => i.Recommended).Sum(i => i.SizeBytes);
            SetTile(TileJunk, TileJunkSummary, bytes == 0 ? "Nothing to clean" : $"≈ {Sizes.Format(bytes)} can be freed",
                bytes > 100L * 1024 * 1024 ? TileState.Attention : TileState.Ready);
        });

        // 5. Updates – the slowest, last.
        await Step(gen, "Asking winget for updates…", async () =>
        {
            var updates = await Task.Run(WingetUpdates.Check);
            if (!Current()) return;
            if (updates is null)
            {
                SetTile(TileUpdates, TileUpdatesSummary, "winget not found", TileState.Ready);
                return;
            }
            var count = WingetUpdates.Attach(all, updates);
            SetTile(TileUpdates, TileUpdatesSummary, count == 0 ? "Everything is up to date" : $"{count} update(s) available",
                count > 0 ? TileState.Attention : TileState.Ready);
        });

        if (!Current()) return;
        FooterProgress.Value = FooterProgress.Maximum;
        FooterStatus.Text = Notices.Count > 0 ? $"Done – {Notices.Count} notice(s) on the right" : "Done – everything counted";
    }

    /// <summary>Runs one summary step; a failing step only marks its own tiles, never the whole screen.</summary>
    private async Task Step(int gen, string text, Func<Task> work)
    {
        if (gen != _generation) return;
        FooterStatus.Text = text;
        try { await work(); }
        catch (Exception)
        {
            foreach (var (tile, summary) in Tiles().Where(t => (string?)t.Tile.Tag == nameof(TileState.Loading)))
                if (text.Contains("winget") ? tile == TileUpdates : text.Contains("junk") ? tile == TileJunk
                    : text.Contains("startup") ? tile == TileStartup : text.Contains("bloatware") ? tile == TileBloatware : true)
                    SetTile(tile, summary, "Couldn't count – open to see", TileState.Ready);
        }
        if (gen == _generation) FooterProgress.Value = Math.Min(FooterProgress.Maximum, ++_steps * FooterProgress.Maximum / 6.0);
    }

    private IEnumerable<(Button Tile, TextBlock Summary)> Tiles() => new[]
    {
        (TileUninstall, TileUninstallSummary), (TileLeftovers, TileLeftoversSummary), (TileJunk, TileJunkSummary),
        (TileUpdates, TileUpdatesSummary), (TileStartup, TileStartupSummary), (TileTrial, TileTrialSummary),
        (TileTimeline, TileTimelineSummary), (TileUndo, TileUndoSummary), (TileBloatware, TileBloatwareSummary),
        (TileDrivers, TileDriversSummary),
    };

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 3 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(3))} and {list.Count - 3} more";
    }

    private async Task<List<InstalledProgram>> ProgramsAsync() => _programs ??= await Task.Run(ProgramInventory.LoadWithRecentlyRemoved);

    /// <summary>Once per session: is a newer Purgely out, and does Purgely run under the signed-in account?</summary>
    private async Task SessionChecksAsync()
    {
        if (_sessionChecked) return;
        _sessionChecked = true;
        try
        {
            if (await Task.Run(DataFolders.DifferentUser) is { } users)
                Notices.Add(new Notice(Warning,
                    $"Purgely runs as {users.Running}, but {users.SignedIn} is signed in. Programs installed for everyone are " +
                    "shown, but settings, AppData and startup items are those of the administrator account, not yours.",
                    "What to do", NoticeKind.DifferentUser, IsWarning: true));

            if (await UpdateCheck.CheckIfDueAsync() is { } release)
                Notices.Insert(0, new Notice(Info,
                    $"Purgely {release.Version}{(release.IsPreview ? " (beta)" : "")} is available – you have {UpdateCheck.CurrentVersion}. " +
                    "Download it and replace your Purgely.exe. Dismiss to skip this version.",
                    "Open download page", NoticeKind.UpdateAvailable, Payload: release));
        }
        catch (Exception) { /* background checks never disturb the home screen */ }
    }

    // ================================================================== welcome tour
    private static readonly (string Icon, string Title, string Text)[] WelcomePages =
    {
        ("\uE80F", "Welcome to Purgely",
         "One free tool that removes programs cleanly – leftovers included – gets rid of bloatware and ads, and keeps " +
         "your programs and drivers up to date. No ads, no subscriptions, no tracking.\n\n" +
         "This is a beta version. If something looks wrong, tell us: the gear button at the top right → Report a problem."),
        ("\uE7B3", "Nothing changes until you say so",
         "Purgely looks first and shows you what it found – and how sure it is, in words (\"Very sure · 88%\"). " +
         "Only what it is sure about is ticked. Anything another program still uses is marked \"Keep (shared)\" and never removed."),
        ("\uE7A7", "Almost everything can be undone",
         "Before Purgely removes or changes anything, it saves a backup. The Undo center puts it back in one click.\n\n" +
         "A good first step: Checkup looks at the whole PC at once and fixes only what Purgely is sure about."),
    };

    private int _welcomePage;

    private void ShowWelcome()
    {
        _welcomePage = 0;
        WelcomeOverlay.Visibility = Visibility.Visible;
        ShowWelcomePage();
    }

    private void ShowWelcomePage()
    {
        var (icon, title, text) = WelcomePages[_welcomePage];
        WelcomeIcon.Text = icon;
        WelcomeTitle.Text = title;
        WelcomeText.Text = UpdateCheck.CurrentVersion.Major >= 1 ? text.Split("\n\nThis is a beta")[0] : text;
        WelcomeStep.Text = $"{_welcomePage + 1} of {WelcomePages.Length}";
        WelcomeBack.Visibility = _welcomePage == 0 ? Visibility.Collapsed : Visibility.Visible;
        var last = _welcomePage == WelcomePages.Length - 1;
        WelcomeNext.Content = last ? "Run Checkup" : "Next";
        WelcomeSkip.Content = last ? "Close" : "Skip tour";
        WelcomeDots.Children.Clear();
        for (var i = 0; i < WelcomePages.Length; i++)
            WelcomeDots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = i == _welcomePage ? 22 : 8, Height = 8, Margin = new Thickness(0, 0, 6, 0),
                Fill = (System.Windows.Media.Brush)FindResource(i == _welcomePage ? "AccentBrush" : "CardPressedBrush"),
            });
        Dispatcher.BeginInvoke(() => WelcomeNext.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseWelcome()
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        AppSettings.Current.WelcomeSeen = true;
        AppSettings.Save();
        TileUninstall.Focus();
    }

    private void WelcomeSkip_Click(object sender, RoutedEventArgs e) => CloseWelcome();

    private void WelcomeBack_Click(object sender, RoutedEventArgs e)
    {
        if (_welcomePage > 0) { _welcomePage--; ShowWelcomePage(); }
    }

    private void WelcomeNext_Click(object sender, RoutedEventArgs e)
    {
        if (_welcomePage < WelcomePages.Length - 1) { _welcomePage++; ShowWelcomePage(); return; }
        CloseWelcome();
        OpenPage(new CheckupWindow());
    }

    private bool WelcomeOpen => WelcomeOverlay.Visibility == Visibility.Visible;

    // ================================================================== tiles
    /// <summary>
    /// Opens a feature as a "page": it takes the home window's place (same position, size and state) and the home
    /// window hides. Closing the page – its Home button, Esc or the X – brings the home screen back in the same place.
    /// </summary>
    private void OpenPage(Window page)
    {
        if (_page is { IsLoaded: true }) _page.Close();
        _page = page;
        page.Closed += (_, _) => { if (ReferenceEquals(_page, page)) _page = null; };
        PageNav.Open(this, page, () => _ = RefreshAsync());
    }

    private Window? _page;

    private void OpenMain(MainView view, string search = "") => OpenPage(new MainWindow(view, search));

    private void Uninstall_Click(object sender, RoutedEventArgs e) => OpenMain(MainView.All);
    private void Leftovers_Click(object sender, RoutedEventArgs e) => OpenMain(MainView.Leftovers);
    private void Updates_Click(object sender, RoutedEventArgs e) => OpenMain(MainView.Updates);

    private async void Junk_Click(object sender, RoutedEventArgs e)
    {
        if (_junk is null)
        {
            SetTile(TileJunk, TileJunkSummary, "Looking…", TileState.Loading);
            var all = await ProgramsAsync();
            try { _junk = await Task.Run(() => InstallerJunkScanner.Scan(all)); }
            catch (Exception ex)
            {
                SetTile(TileJunk, TileJunkSummary, "Couldn't count – try again", TileState.Ready);
                Dialog.Show(this, $"The installer junk scan failed:\n\n{ex.Message}", "Installer junk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var items = _junk;
        _junk = null; // cleaned items must be counted again next time
        OpenPage(new ScanResultsWindow("Installer junk", items, null, null, heading: "Hidden installer junk"));
    }

    private async void Startup_Click(object sender, RoutedEventArgs e) => await OpenStartupAsync();

    private async Task OpenStartupAsync() => OpenPage(new StartupWindow(await ProgramsAsync()));

    private void Checkup_Click(object sender, RoutedEventArgs e) => OpenPage(new CheckupWindow());

    private void Move_Click(object sender, RoutedEventArgs e) => OpenPage(new MoveWindow());

    private void Repair_Click(object sender, RoutedEventArgs e) => OpenPage(new RepairWindow());

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var page = new SettingsWindow();
        OpenPage(page);
        page.Closed += (_, _) => { if (page.WelcomeRequested) ShowWelcome(); };
    }

    private void Bloatware_Click(object sender, RoutedEventArgs e) => OpenPage(new BloatwareWindow());

    private void Drivers_Click(object sender, RoutedEventArgs e) => OpenPage(new HardwareWindow());

    private void Trial_Click(object sender, RoutedEventArgs e) => OpenPage(new TrialWindow());

    private void Timeline_Click(object sender, RoutedEventArgs e) => OpenPage(new TimelineWindow());

    private void Undo_Click(object sender, RoutedEventArgs e) => OpenPage(new UndoWindow());

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _programs = null;
        _junk = null;
        await RefreshAsync();
    }

    // ================================================================== notices
    private async void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Notice notice) return;
        switch (notice.Kind)
        {
            case NoticeKind.Bundleware:
                OpenPage(new TimelineWindow(warningsOnly: true));
                break;
            case NoticeKind.NewStartupItems:
                await OpenStartupAsync();
                break;
            case NoticeKind.ShouldBeOn when notice.Payload is List<StartupItem> keep:
                var on = await Task.Run(() =>
                {
                    StartupManager.ForgetKeepOff(keep);
                    return keep.Count(i => StartupManager.Enable(i).Ok);
                });
                FooterStatus.Text = $"Turned {on} startup item(s) back on";
                break;
            case NoticeKind.ClutterBack:
                OpenPage(new BloatwareWindow());
                break;
            case NoticeKind.UpdateAvailable when notice.Payload is PurgelyRelease release:
                UpdateCheck.OpenReleasePage(release.PageUrl);
                return; // stays until the new version is running or the user skips it
            case NoticeKind.DifferentUser:
                Dialog.Show(this,
                    "Purgely is running under a different account from the one that's signed in.\n\n" +
                    "This happens when you start Purgely from a standard account and type an administrator's password. " +
                    "Programs installed for all users are handled normally, but your own settings, AppData folders and " +
                    "startup items belong to the account you are signed in with, and Purgely can't see them from here.\n\n" +
                    "For a complete cleanup, sign in with an administrator account and start Purgely there – or give your own " +
                    "account administrator rights for the cleanup.",
                    "Different account", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            case NoticeKind.KeptOff when notice.Payload is List<StartupItem> items:
                var count = await Task.Run(() => StartupManager.TurnOffAgain(items));
                FooterStatus.Text = $"Turned {count} startup item(s) off again";
                break;
        }
        Notices.Remove(notice);
    }

    private void NoticeDismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Notice notice) return;
        // Dismissing "turned on again" means the user wants it on – stop keeping it off.
        // Dismiss = "leave it as it is now" – stop keeping it off, so the notice doesn't come back.
        if (notice is { Kind: NoticeKind.KeptOff or NoticeKind.ShouldBeOn, Payload: List<StartupItem> items })
            StartupManager.ForgetKeepOff(items);
        if (notice is { Kind: NoticeKind.ClutterBack, Payload: ValueTuple<List<ClutterTweak>, List<InstalledProgram>> back })
            Bloatware.Forget(back.Item1, back.Item2);
        if (notice is { Kind: NoticeKind.UpdateAvailable, Payload: PurgelyRelease skipped })
        {
            AppSettings.Current.SkippedVersion = skipped.Tag;
            AppSettings.Save();
        }
        Notices.Remove(notice);
    }

    // ================================================================== keyboard
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WelcomeOpen)
        {
            if (e.Key == Key.Escape) { CloseWelcome(); e.Handled = true; }
            // Alt+letter must not open a tile behind the tour (Alt+F4 and Alt+Space keep working).
            else if (e.Key == Key.System && e.SystemKey is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9) e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            Refresh_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenMain(MainView.All);
            e.Handled = true;
        }
    }

    /// <summary>Typing a letter opens the program list with it already in the search box.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (WelcomeOpen) { e.Handled = true; return; }
        if (string.IsNullOrEmpty(e.Text) || !char.IsLetterOrDigit(e.Text[0])) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        OpenMain(MainView.All, e.Text);
        e.Handled = true;
    }
}
