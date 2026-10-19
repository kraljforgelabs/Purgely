using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Purgely.Core;

namespace Purgely;

/// <summary>Preinstalled apps with a verdict, and Windows ads/suggestions switches – all with Undo.</summary>
public partial class BloatwareWindow : Window
{
    private List<BloatItem> _apps = new();
    private List<ClutterTweak> _tweaks = new();
    private ListCollectionView? _appsView;

    /// <param name="startTab">0 = Preinstalled apps, 1 = Ads, AI &amp; privacy, 2 = Browser extensions.</param>
    public BloatwareWindow(int startTab = 0)
    {
        InitializeComponent();
        Tabs.SelectedIndex = Math.Clamp(startTab, 0, 2);
        BusyGuard.Attach(this);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        ShowBusy("Looking for bloatware…");
        try
        {
            (_apps, _tweaks) = await Task.Run(() =>
            {
                var programs = ProgramInventory.LoadWithRecentlyRemoved();
                return (Bloatware.Scan(programs), Bloatware.ReadTweaks());
            });
        }
        catch (Exception ex)
        {
            Dialog.Show(this, "Could not read the installed apps.\n\n" + ex.Message, "Bloatware",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { HideBusy(); }

        foreach (var t in _tweaks) t.IsChecked = t.IsOn && t.TickByDefault;
        _appsView = new ListCollectionView(_apps) { Filter = o => ShowKeep.IsChecked == true || ((BloatItem)o).Rating != BloatRating.Keep };
        AppsGrid.ItemsSource = _appsView;
        var tweaksView = new ListCollectionView(_tweaks);
        tweaksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClutterTweak.Category)));
        TweaksGrid.ItemsSource = tweaksView;
        UpdateCounts();
    }

    private void ShowBusy(string title) { BusyTitle.Text = title; BusyOverlay.Visibility = Visibility.Visible; }
    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    private void Filter_Changed(object sender, RoutedEventArgs e) { _appsView?.Refresh(); UpdateCounts(); }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateCounts();

    private void UpdateCounts()
    {
        var remove = _apps.Count(a => a.Rating == BloatRating.Remove);
        var check = _apps.Count(a => a.Rating == BloatRating.Check);
        AppsCount.Text = _apps.Count == 0 ? "No known bloatware found – nice and clean."
            : $"{remove} safe to remove · {check} to check · {_apps.Count(a => a.IsChecked)} ticked";
        var on = _tweaks.Count(t => t.IsOn);
        TweaksCount.Text = on == 0 ? "All of these are off." : $"{on} of {_tweaks.Count} are on · {_tweaks.Count(t => t.IsChecked && t.IsOn)} ticked";
    }

    private async void RemoveApps_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _apps.Where(a => a.IsChecked && a.Status != "Removed").ToList();
        if (chosen.Count == 0)
        {
            Dialog.Show(this, "Tick the apps you want to remove first.", "Bloatware", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var keep = chosen.Where(a => a.Rating == BloatRating.Keep).Select(a => a.Name).ToList();
        var programs = chosen.Where(a => !a.Program.IsStoreApp).ToList();
        var store = chosen.Where(a => a.Program.IsStoreApp).ToList();

        var text = $"Remove {chosen.Count} app(s)?\n\n" + string.Join("\n", chosen.Take(12).Select(a => "• " + a.Name)) +
                   (chosen.Count > 12 ? $"\n• …and {chosen.Count - 12} more" : "") +
                   "\n\nStore apps are removed for your account; each gets an Undo entry that reopens the Microsoft Store " +
                   "page so you can reinstall it." +
                   (programs.Count > 0 ? "\nPrograms run their own uninstaller one by one, then Purgely looks for leftovers." : "") +
                   (keep.Count > 0 ? "\n\nWarning – Purgely recommends keeping: " + string.Join(", ", keep) : "");
        if (Dialog.Show(this, text, "Remove bloatware", MessageBoxButton.YesNo,
                keep.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        ShowBusy("Removing apps…");
        try
        {
            foreach (var app in store)
            {
                BusyTitle.Text = $"Removing {app.Name}…";
                var (ok, message) = await Task.Run(() => StoreApps.Remove(app.Program));
                app.Status = ok ? "Removed" : "Failed: " + message;
                if (ok) app.IsChecked = false;
            }
        }
        finally { HideBusy(); }

        // Programs go through the normal uninstall flow (official uninstaller + leftover scan).
        foreach (var app in programs)
        {
            var dialog = new UninstallWindow(app.Program) { Owner = this };
            var decided = dialog.ShowDialog() == true;
            app.Status = !decided ? "Cancelled"
                : dialog.Outcome == UninstallOutcome.Removed ? "Uninstalled – its leftovers are in Clean leftovers"
                : "Not finished – use Uninstall programs to finish it";
            if (decided && dialog.Outcome == UninstallOutcome.Removed) app.IsChecked = false;
        }
        UpdateCounts();
    }

    private void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _tweaks.Where(t => t.IsChecked && t.IsOn).ToList();
        if (chosen.Count == 0)
        {
            Dialog.Show(this, "Tick what you want to turn off first.", "Ads, AI & privacy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var results = Bloatware.TurnOff(chosen);
        foreach (var (tweak, ok, message) in results)
        {
            tweak.Status = ok ? "Turned off" : message;
            if (ok) { tweak.IsOn = false; tweak.IsChecked = false; }
        }
        TweaksGrid.Items.Refresh();
        UpdateCounts();
        Dialog.Show(this,
            $"{results.Count(r => r.Ok)} of {results.Count} turned off.\n\n" +
            "Some changes appear after you sign out and back in (or restart Explorer). " +
            "To turn them back on, open the Undo center and restore \"Windows clutter\".",
            "Ads, AI & privacy", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ browser extensions (read-only)
    private bool _extensionsLoaded;

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs) || Tabs.SelectedItem != ExtensionsTab || _extensionsLoaded) return;
        _extensionsLoaded = true;
        ShowBusy("Reading browser extensions…");
        List<BrowserExtension> list;
        try { list = await Task.Run(BrowserExtensions.List); }
        catch (Exception) { list = new(); }
        finally { HideBusy(); }
        var view = new ListCollectionView(list);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BrowserExtension.Group)));
        ExtensionsGrid.ItemsSource = view;
        var risky = list.Count(x => x.IsRisky);
        ExtensionsCount.Text = list.Count == 0 ? "No extensions found." : $"{list.Count} extension(s) · {risky} worth a look";
    }

    private BrowserExtension? SelectedExtension()
    {
        if (ExtensionsGrid.SelectedItem is BrowserExtension x) return x;
        Dialog.Show(this, "Select an extension in the list first.", "Browser extensions", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void ManageExtension_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExtension() is not { } x) return;
        try { BrowserExtensions.OpenManager(x); }
        catch (Exception ex)
        {
            Dialog.Show(this, $"Could not open {x.Browser}.\n\n{ex.Message}", "Browser extensions", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExtensionStore_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExtension() is not { } x) return;
        try { BrowserExtensions.OpenStorePage(x); } catch (Exception) { }
    }

    /// <summary>Back from the Undo center the page reads everything again, so restored switches show "On".</summary>
    private void OpenUndo_Click(object sender, RoutedEventArgs e) => PageNav.Open(this, new UndoWindow(), () => _ = ReloadAsync());
}
