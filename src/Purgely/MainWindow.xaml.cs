using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Purgely.Core;
using Purgely.Core.Startup;

namespace Purgely;

/// <summary>Which part of the program list the home screen opened.</summary>
public enum MainView { All, Leftovers, Updates }

public partial class MainWindow : Window
{
    private List<InstalledProgram> _programs = new();
    private ICollectionView? _view;

    /// <summary>Raised after programs were uninstalled, cleaned or updated, so the home screen can recount.</summary>
    public event Action? Changed;

    public MainWindow(MainView view = MainView.All, string search = "")
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        AdminBadge.Text = IsAdministrator() ? "Administrator" : "Not running as administrator";
        OrphansOnly.IsChecked = view == MainView.Leftovers;
        UpdatesOnly.IsChecked = view == MainView.Updates;
        ApplyMode();
        Loaded += async (_, _) =>
        {
            SearchBox.Text = search;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.Focus();
            await LoadProgramsAsync();
        };
        Closed += (_, _) => Changed?.Invoke();
    }

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private async Task LoadProgramsAsync()
    {
        StatusText.Text = "Loading programs…";
        _programs = await Task.Run(ProgramInventory.LoadWithRecentlyRemoved);
        _view = CollectionViewSource.GetDefaultView(_programs);
        _view.Filter = Matches;
        ProgramsGrid.ItemsSource = _view;
        UpdateStatus();
        _ = AnalyzeDependenciesAsync(_programs);
        _ = SuggestAsync(_programs);
        await CheckUpdatesAsync(_programs);
    }

    private void Startup_Click(object sender, RoutedEventArgs e) =>
        new StartupWindow(_programs) { Owner = this }.Show();

    private void Trial_Click(object sender, RoutedEventArgs e) =>
        new TrialWindow(async () => await LoadProgramsAsync()) { Owner = this }.Show();

    private void Timeline_Click(object sender, RoutedEventArgs e) =>
        new TimelineWindow { Owner = this }.ShowDialog();

    /// <summary>Dependency Map runs in the background after the list is shown.</summary>
    private async Task AnalyzeDependenciesAsync(List<InstalledProgram> programs)
    {
        ShowBusy("Analyzing dependencies…");
        var clock = Stopwatch.StartNew();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!ReferenceEquals(programs, _programs) || p.Total == 0) return;
            if (!_busyUi && p.Done < p.Total) BusyPanel.Visibility = Visibility.Visible; // a scan may have hidden it
            var percent = p.Done * 100 / p.Total;
            BusyBar.IsIndeterminate = false;
            BusyBar.Value = percent;
            var text = $"Analyzing dependencies {percent}%";
            if (p.Done >= 3 && p.Done < p.Total)
            {
                var left = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds * (double)(p.Total - p.Done) / p.Done);
                text += left.TotalSeconds < 60 ? $" · about {Math.Max(1, (int)left.TotalSeconds)} s left"
                                               : $" · about {(int)Math.Ceiling(left.TotalMinutes)} min left";
            }
            BusyText.Text = text;
        });

        try { await Task.Run(() => DependencyMap.Analyze(programs, progress)); }
        catch (Exception) { /* the map is optional */ }

        if (!ReferenceEquals(programs, _programs)) return;
        HideBusy();
        _view?.Refresh();
        UpdateStatus();
    }

    private int _updatesAvailable;
    private bool _wingetMissing;

    /// <summary>Smart suggestions: unused programs and several programs doing the same job (hints only).</summary>
    private async Task SuggestAsync(List<InstalledProgram> programs)
    {
        int count;
        try { count = await Task.Run(() => Suggestions.Analyze(programs)); }
        catch (Exception) { return; }
        if (!ReferenceEquals(programs, _programs)) return;
        SuggestedOnly.Content = count > 0 ? $"Suggestions ({count})" : "Suggestions";
        _view?.Refresh();
    }

    /// <summary>Asks winget which programs have a newer version (runs in the background).</summary>
    private async Task CheckUpdatesAsync(List<InstalledProgram> programs)
    {
        List<WingetUpdate>? updates;
        try { updates = await Task.Run(WingetUpdates.Check); }
        catch (Exception) { updates = null; }
        if (!ReferenceEquals(programs, _programs)) return;
        _wingetMissing = updates is null;
        _updatesAvailable = updates is null ? 0 : WingetUpdates.Attach(programs, updates);
        _view?.Refresh();
        UpdateStatus();
    }

    private void Drivers_Click(object sender, RoutedEventArgs e) => PageNav.Open(this, new HardwareWindow());

    /// <summary>Updates every program that has an update, one after another, in one progress window.</summary>
    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        // Ticked programs only, when some are ticked – otherwise every program with an update.
        var all = _programs.Where(p => p.WingetId is not null && !p.IsOrphan).ToList();
        if (all.Any(p => p.IsChecked)) all = all.Where(p => p.IsChecked).ToList();
        var todo = all.Where(p => !p.WingetFailed).ToList();
        var skipped = all.Where(p => p.WingetFailed).ToList();
        if (todo.Count == 0)
        {
            Dialog.Show(this, skipped.Count > 0
                    ? "The remaining programs could not be updated by winget last time – update them one at a time."
                    : "Everything is up to date.", "Update all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Dialog.Show(this,
                $"Update {todo.Count} program(s), one after another?\n\n" +
                string.Join("\n", todo.Take(15).Select(p => $"• {p.Name}  {p.Version} → {p.AvailableVersion}")) +
                (todo.Count > 15 ? $"\n… and {todo.Count - 15} more" : "") +
                (skipped.Count > 0 ? $"\n\nSkipped (winget couldn't update them before): {string.Join(", ", skipped.Select(p => p.Name))}" : "") +
                "\n\nClose the programs first if you can – an open program may not update. This can take a while.",
                "Update all", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        new UpdateWindow(todo) { Owner = this }.ShowDialog();
        await LoadProgramsAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        if (p.WingetId is null)
        {
            Dialog.Show(this, _wingetMissing
                    ? $"The update check did not work:\n\n{WingetUpdates.LastError}"
                    : $"No update was found for \"{p.Name}\".\n\n(winget knows most popular programs, but not all of them.)",
                "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (p.WingetFailed &&
            Dialog.Show(this, $"Last time winget could not update \"{p.Name}\".\n\n" +
                                  "Some programs must be updated with their own updater or from the publisher's website.\n\nTry winget again anyway?",
                "Update", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        // The update runs hidden; the dialog shows download, verification and install progress.
        new UpdateWindow(p) { Owner = this }.ShowDialog();
        await LoadProgramsAsync();
    }

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyBar.IsIndeterminate = true;
        BusyPanel.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyPanel.Visibility = Visibility.Collapsed;

    private bool Matches(object item)
    {
        var p = (InstalledProgram)item;
        if (OrphansOnly.IsChecked == true && !p.IsOrphan) return false;
        if (UpdatesOnly.IsChecked == true && p.WingetId is null) return false;
        if (SuggestedOnly.IsChecked == true && !p.HasSuggestion) return false;
        var q = SearchBox.Text.Trim();
        return q.Length == 0
               || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || p.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateEmptyState()
    {
        if (EmptyState is null) return;
        var empty = OrphansOnly.IsChecked == true && SearchBox.Text.Trim().Length == 0 &&
                    (_view is null || !_view.Cast<object>().Any());
        EmptyState.Visibility = empty && _view is not null ? Visibility.Visible : Visibility.Collapsed;

        var nothing = _view is not null && !empty && _programs.Count > 0 && !_view.Cast<object>().Any();
        NoMatchState.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        if (!nothing) return;
        var q = SearchBox.Text.Trim();
        NoMatchText.Text = q.Length > 0 ? $"No program matches \"{q}\"." :
                           SuggestedOnly.IsChecked == true ? "No suggestions – nothing unused for months and no duplicates found." :
                           UpdatesOnly.IsChecked == true ? "Everything is up to date." : "Nothing to show.";
        NoMatchHint.Text = q.Length > 0
            ? "Removed long ago? Click \"Find leftovers by name\" to search for its traces."
            : SuggestedOnly.IsChecked == true ? "Purgely reads when programs last ran from Windows' Prefetch – that needs administrator rights." : "";
    }

    /// <summary>Whole-PC sweep: dead shortcuts, startup entries, tasks, services and empty folders of programs removed long ago.</summary>
    private async void Sweep_Click(object sender, RoutedEventArgs e)
    {
        ShowBusy("Sweeping the PC for orphaned leftovers…");
        Cursor = Cursors.Wait;
        SetBusyUi(true);
        List<LeftoverItem> items;
        try
        {
            var all = _programs;
            var progress = new Progress<string>(t => BusyText.Text = t);
            items = await Task.Run(() => OrphanSweep.Scan(all, progress));
        }
        finally
        {
            Cursor = null;
            SetBusyUi(false);
            HideBusy();
            UpdateStatus();
        }
        if (!IsLoaded) return; // the window was closed during the scan
        PageNav.Open(this, new ScanResultsWindow("Orphaned leftovers", items, null, async () => await LoadProgramsAsync(),
            heading: "Orphaned leftovers – traces of programs removed long ago"));
    }

    private void UpdateStatus()
    {
        UpdateEmptyState();
        var shown = _view?.Cast<object>().Count() ?? 0;
        var orphans = _programs.Count(p => p.IsOrphan);
        StatusText.Text = $"Showing {shown} of {_programs.Count} programs  •  removed, to check: {orphans}" +
                          (_updatesAvailable > 0 ? $"  •  updates available: {_updatesAvailable}" : "");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ---------------------------------------------------------------- keyboard shortcuts
    /// <summary>
    /// Locks the page during a scan or cleanup. Not Window.IsEnabled = false: the Fluent theme paints a disabled
    /// DataGrid white. The list is dimmed and the buttons are disabled instead.
    /// </summary>
    private void SetBusyUi(bool busy)
    {
        _busyUi = busy;
        ActionsPanel.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        ProgramsGrid.IsHitTestVisible = !busy;
        ProgramsGrid.Opacity = busy ? 0.45 : 1;
    }

    private bool _busyUi;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busyUi) { e.Handled = true; return; }
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var inSearch = SearchBox.IsKeyboardFocusWithin;

        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && !inSearch)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            SearchBox.Paste();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(); // back to the home screen
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Refresh_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !inSearch && ProgramsGrid.SelectedItem is not null)
        {
            Uninstall_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && inSearch && ProgramsGrid.Items.Count > 0)
        {
            // Arrow down from the search box jumps into the list.
            ProgramsGrid.SelectedIndex = Math.Max(ProgramsGrid.SelectedIndex, 0);
            ProgramsGrid.ScrollIntoView(ProgramsGrid.SelectedItem);
            ProgramsGrid.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Typing anywhere in the window goes into the search box.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyMode();
        ApplyFilter();
    }

    /// <summary>Shows only the title, hint and buttons that fit what the list is showing right now.</summary>
    private void ApplyMode()
    {
        if (PageTitleText is null) return; // called while the window is still being built
        var leftovers = OrphansOnly.IsChecked == true;
        var updates = UpdatesOnly.IsChecked == true && !leftovers;
        PageTitleText.Text = leftovers ? "Clean leftovers" : updates ? "Program updates" : "Uninstall programs";
        PageSubtitleText.Text = leftovers
            ? "Programs that are gone but left things behind. Or type any name and click \"Find leftovers by name\"."
            : updates ? "Newer versions of your programs from Windows Package Manager (winget). Tick the ones you want, or click \"Update all\". Drivers are on the Driver updates page."
            : "Select a program to uninstall it or clean up what it left behind. Double-click to uninstall.";
        StatusHint.Text = "";
        SetVisible(UninstallButton, !leftovers && !updates);
        SetVisible(ScanButton, !updates);
        SetVisible(ScanByNameButton, !updates);
        SetVisible(UpdateButton, updates);
        SetVisible(UpdateAllButton, updates);
        SetVisible(DriversButton, false); // drivers have their own page (home → Drivers) – one place for each
        SetVisible(SweepButton, leftovers);
        UpdateEmptyState();
        Title = $"Purgely – {PageTitleText.Text}";
    }

    private static void SetVisible(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void Home_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyFilter()
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateStatus();
    }

    private InstalledProgram? Selected()
    {
        if (ProgramsGrid.SelectedItem is InstalledProgram p) return p;
        Dialog.Show(this, "Select a program in the list first.", "Purgely",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadProgramsAsync();

    /// <summary>Double-click always means "this one program", even when others are ticked.</summary>
    private void ProgramsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return; // fast clicks on a tick box
        _singleOnly = true;
        Uninstall_Click(sender, e);
    }

    private bool _singleOnly;

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ------------------------------------------------------------------ bulk uninstall
    private List<InstalledProgram> Checked() => _programs.Where(p => p.IsChecked).ToList();

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateBulkButton();

    private void UpdateBulkButton()
    {
        var count = _programs.Count(p => p.IsChecked);
        UninstallText.Text = count > 0 ? $"Uninstall {count} ticked" : "Uninstall";
        UpdateAllText.Text = count > 0 ? $"Update {count} ticked" : "Update all";
        ScanText.Text = count > 0 ? $"Scan {count} ticked" : "Scan leftovers";
        ClearChecksButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Header tick box: ticks every program the list shows right now (search and filters apply).</summary>
    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        var tick = CheckAllBox.IsChecked == true;
        foreach (var p in _view?.Cast<InstalledProgram>() ?? Enumerable.Empty<InstalledProgram>()) p.IsChecked = tick;
        _view?.Refresh();
        UpdateBulkButton();
    }

    private void ClearChecks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _programs) p.IsChecked = false;
        CheckAllBox.IsChecked = false;
        _view?.Refresh();
        UpdateBulkButton();
    }

    private async Task BulkUninstallAsync(List<InstalledProgram> chosen)
    {
        var runtimes = chosen.Where(p => p.IsRuntime && p.UsedBy.Count > 0).ToList();
        var text = $"Uninstall these {chosen.Count} programs, one after another?\n\n" +
                   string.Join("\n", chosen.Take(15).Select(p => "• " + p.Name)) +
                   (chosen.Count > 15 ? $"\n• …and {chosen.Count - 15} more" : "") +
                   "\n\nAt the end Purgely scans for the leftovers of all of them at once." +
                   (runtimes.Count > 0
                       ? "\n\nWarning – other programs need: " + string.Join(", ", runtimes.Select(r => r.Name)) +
                         ". Removing them will probably break those programs."
                       : "") +
                   "\n\nThe programs' own uninstallers can't be undone by Purgely.";
        if (Dialog.Show(this, text, "Uninstall several programs", MessageBoxButton.YesNo,
                runtimes.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var window = new BulkUninstallWindow(chosen) { Owner = this };
        var scan = window.ShowDialog() == true;
        var removed = window.Removed;
        foreach (var p in _programs) p.IsChecked = false;
        await LoadProgramsAsync();
        UpdateBulkButton();
        if (scan && removed.Count > 0)
            await RunSuiteScanAsync($"{removed.Count} programs", removed, bulk: true);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var singleOnly = _singleOnly;
        _singleOnly = false;
        if (!singleOnly && Checked() is { Count: > 0 } ticked)
        {
            await BulkUninstallAsync(ticked);
            return;
        }
        if (Selected() is not { } p) return;

        if (p.IsStoreApp)
        {
            if (Dialog.Show(this, $"Remove the Store app \"{p.Name}\"?\n\n" +
                                  "Windows removes the app and its data. Purgely can't undo this, but you can reinstall it " +
                                  "from the Microsoft Store at any time.",
                    "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            ShowBusy($"Removing {p.Name}…");
            var (ok, message) = await Task.Run(() => StoreApps.Remove(p));
            HideBusy();
            if (!ok) Dialog.Show(this, $"Windows could not remove \"{p.Name}\".\n\n{message}", "Uninstall",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            await LoadProgramsAsync();
            return;
        }

        if (p.IsPortable)
        {
            if (Dialog.Show(this, $"\"{p.Name}\" is a portable program – it was never installed, it is just a folder:\n\n{p.InstallLocation}\n\n" +
                                  "Purgely now looks for the folder and anything else it left (settings, shortcuts). You review the list " +
                                  "before anything is removed, and everything goes to the Undo center.",
                    "Portable program", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;
            await RunScanAsync(p.Name, ScanTarget.FromProgram(p, stillInstalled: false), null);
            return;
        }

        if (p.IsOrphan)
        {
            Dialog.Show(this, $"\"{p.Name}\" is already removed ({p.OrphanReason}).\n\n" +
                                  "Use \"Scan leftovers\" to clean what it left behind.",
                "Already removed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (p.IsRuntime && p.UsedBy.Count > 0 &&
            Dialog.Show(this, $"\"{p.Name}\" is needed by:\n\n" +
                                  string.Join("\n", p.UsedBy.Take(12).Select(n => $"• {n}")) +
                                  (p.UsedBy.Count > 12 ? $"\n… and {p.UsedBy.Count - 12} more" : "") +
                                  "\n\nUninstalling it will probably BREAK these programs. Continue anyway?",
                "Dependency Map", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (Dialog.Show(this, $"Uninstall \"{p.Name}\"?\n\n" +
                                  (p.IsRuntime && p.UsedBy.Count == 0
                                      ? "Dependency Map: no installed program was found that uses it.\n" +
                                        "Some programs load runtimes in ways Purgely cannot see, and a runtime's own " +
                                        "uninstaller cannot be undone – if a program stops working, reinstall this runtime " +
                                        "from Microsoft's website.\n\n" : "") +
                                  "1. The program's official uninstaller will run.\n" +
                                  "2. Then Purgely scans for leftovers.",
                "Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // The uninstall dialog runs the official uninstaller and follows it (window, silent or stuck).
        var dialog = new UninstallWindow(p) { Owner = this };
        var decided = dialog.ShowDialog() == true;
        await LoadProgramsAsync();
        if (!decided) return;

        if (dialog.Outcome == UninstallOutcome.Force)
        {
            await RunScanAsync(p.Name, ScanTarget.FromProgram(p, stillInstalled: false),
                "Force uninstall: the official uninstaller didn't finish the job. Purgely found these parts of the program – " +
                "review the list before removing. Everything goes to an Undo package first.");
            Timeline.MarkRemovedHandled(p.RegistryKey);
            return;
        }
        if (dialog.Outcome == UninstallOutcome.StillListed)
        {
            await ScanAsync(p, afterUninstall: true);
            return;
        }

        // Suite cleanup: offer the rest of the bundle (e.g. SOLIDWORKS Visualize after SOLIDWORKS).
        var removed = !_programs.Any(q => q.RegistryKey.Equals(p.RegistryKey, StringComparison.OrdinalIgnoreCase) && !q.IsOrphan);
        if (removed)
        {
            var related = SuiteDetector.FindRelated(p.Name, p.Publisher, p.InstallDate, p.InstallLocation, _programs, p.RegistryKey);
            var extra = await OfferRelatedAsync(p.Name, related);
            if (extra.Count > 0)
            {
                await RunSuiteScanAsync(p.Name, new List<InstalledProgram> { p }.Concat(extra).ToList());
                return;
            }
        }
        await ScanAsync(p, afterUninstall: true);
    }

    /// <summary>Shows the related programs, uninstalls the chosen ones one by one, and returns them.</summary>
    private async Task<List<InstalledProgram>> OfferRelatedAsync(string mainName, List<RelatedProgram> related)
    {
        if (related.Count == 0) return new List<InstalledProgram>();
        if (!IsLoaded) return new List<InstalledProgram>();
        var window = new RelatedWindow(mainName, related) { Owner = this };
        if (window.ShowDialog() != true) return new List<InstalledProgram>();

        var done = new List<InstalledProgram>();
        for (var i = 0; i < window.Chosen.Count; i++)
        {
            var program = window.Chosen[i];
            ShowBusy($"Uninstalling {i + 1} of {window.Chosen.Count}: {program.Name}…");
            try
            {
                var waited = await UninstallCommand.RunAndWaitAsync(program);
                Timeline.Log(TimelineEventType.Uninstalled, program.Name, $"Removed as part of the {mainName} suite");
                if (!waited)
                    Dialog.Show(this, $"Follow the steps in the uninstaller of \"{program.Name}\".\n\nClick OK when it has FINISHED.",
                        "Waiting for the uninstaller", MessageBoxButton.OK, MessageBoxImage.Information);
                done.Add(program);
            }
            catch (Exception ex)
            {
                Dialog.Show(this, $"Could not uninstall \"{program.Name}\":\n\n{ex.Message}", "Uninstall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        HideBusy();
        await LoadProgramsAsync();
        return done;
    }

    /// <summary>One combined leftover scan for a whole suite.</summary>
    private async Task RunSuiteScanAsync(string title, List<InstalledProgram> removed, bool bulk = false)
    {
        ShowBusy(bulk ? $"Scanning for leftovers of {title}…" : $"Scanning for leftovers of {title} and {removed.Count - 1} related program(s)…");
        Cursor = Cursors.Wait;
        List<LeftoverItem> items;
        try
        {
            var all = _programs;
            items = await Task.Run(() =>
            {
                var merged = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var program in removed)
                {
                    var stillThere = all.Any(q => q.RegistryKey.Equals(program.RegistryKey, StringComparison.OrdinalIgnoreCase) && !q.IsOrphan);
                    foreach (var item in LeftoverScanner.Scan(ScanTarget.FromProgram(program, stillThere), all))
                    {
                        var key = $"{item.Kind}|{item.Location}";
                        if (!merged.TryGetValue(key, out var existing) || existing.Score < item.Score) merged[key] = item;
                    }
                }
                foreach (var program in removed) Timeline.MarkRemovedHandled(program.RegistryKey);
                return merged.Values.OrderBy(i => i.IsShared).ThenByDescending(i => i.Score).ToList();
            });
        }
        finally
        {
            Cursor = null;
            HideBusy();
            UpdateStatus();
        }
        if (!IsLoaded) return; // the window was closed during the scan
        PageNav.Open(this, new ScanResultsWindow(bulk ? title : $"{title} suite", items, null, async () => await LoadProgramsAsync(),
            heading: bulk ? $"Leftovers of {title}" : $"Leftovers: {title} and related programs"));
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        // Several ticked: one combined leftover scan (e.g. all parts of a suite that was removed earlier).
        if (Checked() is { Count: > 0 } ticked)
        {
            foreach (var t in _programs) t.IsChecked = false;
            CheckAllBox.IsChecked = false;
            UpdateBulkButton();
            await RunSuiteScanAsync($"{ticked.Count} programs", ticked, bulk: true);
            return;
        }
        if (Selected() is not { } p) return;
        await ScanAsync(p, afterUninstall: false);
    }

    private async void ScanByName_Click(object sender, RoutedEventArgs e)
    {
        var name = SearchBox.Text.Trim();
        var target = ScanTarget.FromName(name);
        if (Keywords.Norm(name).Length < 3 || target.IsEmpty)
        {
            Dialog.Show(this, "Type the name of a removed program in the Search box first (for example: Spotify).\n\n" +
                                  "Very generic names like \"Windows\" or \"Update\" are not allowed.",
                "Find leftovers by name", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunScanAsync(name, target, warning: null);

        // Programs from the same suite that are still installed (e.g. "SOLIDWORKS" → SOLIDWORKS Visualize).
        var related = SuiteDetector.FindRelated(name, "", null, "", _programs);
        var extra = await OfferRelatedAsync(name, related);
        if (extra.Count > 0) await RunSuiteScanAsync(name, extra);
    }

    private async Task ScanAsync(InstalledProgram p, bool afterUninstall)
    {
        var stillInstalled = _programs.Any(q =>
            q.RegistryKey.Equals(p.RegistryKey, StringComparison.OrdinalIgnoreCase) && !q.IsOrphan);
        string? warning = stillInstalled
            ? "This program is still INSTALLED" +
              (afterUninstall ? " (the uninstall may have been cancelled or is still finishing)" : "") +
              ". These are its own parts, so nothing is pre-selected – removing them may break it."
            : null;
        await RunScanAsync(p.Name, ScanTarget.FromProgram(p, stillInstalled), warning);
        // A program the Timeline remembered as removed: it has been looked at now, stop listing it.
        if (p.IsRemovedEarlier || afterUninstall) Timeline.MarkRemovedHandled(p.RegistryKey);
    }

    private async Task RunScanAsync(string title, ScanTarget target, string? warning)
    {
        ShowBusy($"Scanning for leftovers of {title}…");
        Cursor = Cursors.Wait;
        SetBusyUi(true);
        List<LeftoverItem> items;
        try
        {
            var all = _programs;
            items = await Task.Run(() => LeftoverScanner.Scan(target, all));
        }
        finally
        {
            Cursor = null;
            SetBusyUi(false);
            HideBusy();
            UpdateStatus();
        }

        if (!IsLoaded) return; // the window was closed during the scan
        var expected = target.ExpectedSizeKb is long kb ? kb * 1024 : (long?)null;
        PageNav.Open(this, new ScanResultsWindow(title, items, warning, async () => await LoadProgramsAsync(),
            expectedBytes: expected));
    }

    private async void Junk_Click(object sender, RoutedEventArgs e)
    {
        ShowBusy("Looking for hidden installer junk…");
        Cursor = Cursors.Wait;
        SetBusyUi(true);
        List<LeftoverItem> items;
        try
        {
            var all = _programs;
            items = await Task.Run(() => InstallerJunkScanner.Scan(all));
        }
        finally
        {
            Cursor = null;
            SetBusyUi(false);
            HideBusy();
            UpdateStatus();
        }
        if (!IsLoaded) return; // the window was closed during the scan
        PageNav.Open(this, new ScanResultsWindow("Installer junk", items, null, async () => await LoadProgramsAsync(),
            heading: "Hidden installer junk"));
    }

    private void Undo_Click(object sender, RoutedEventArgs e) =>
        new UndoWindow { Owner = this }.ShowDialog();
}
