using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Purgely.Core;
using Purgely.Core.Startup;

namespace Purgely;

public partial class StartupWindow : Window
{
    private readonly IReadOnlyList<InstalledProgram> _programs;
    private List<StartupItem> _items = new();
    private ICollectionView? _view;

    public StartupWindow(IReadOnlyList<InstalledProgram> programs)
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        _programs = programs;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "Reading startup items…";
        try
        {
            var programs = _programs;
            var (items, boot) = await Task.Run(() => (StartupScanner.Scan(programs), BootSummary()));
            _items = items;
            BootText.Text = boot;
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        var view = new ListCollectionView(_items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartupItem.Program)));
        view.Filter = Matches;
        _view = view;
        ItemsGrid.ItemsSource = _view;
        UpdateCount();
    }

    /// <summary>"Before and after": Windows' recorded boot times around the first change made in Purgely.</summary>
    private static string BootSummary()
    {
        var boots = BootTimes.Recent(20);
        if (boots.Count == 0)
            return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                       .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                ? "Windows has not recorded any boot times yet."
                : "Boot times are shown when Purgely runs as administrator.";
        var recent = boots.Take(10).ToList();
        var text = $"Windows startup time – last: {boots[0].Seconds:0} s  ·  average of last {recent.Count}: {recent.Average(b => b.Seconds):0} s";
        if (StartupManager.LoadState().FirstChange is { } changed)
        {
            var before = boots.Where(b => b.Time < changed).Take(10).ToList();
            var after = boots.Where(b => b.Time > changed).Take(10).ToList();
            text += after.Count == 0
                ? "  ·  restart the PC to see the effect of your changes"
                : before.Count > 0
                    ? $"  ·  before your changes: {before.Average(b => b.Seconds):0} s → after: {after.Average(b => b.Seconds):0} s"
                    : "";
        }
        return text;
    }

    private bool Matches(object o)
    {
        var i = (StartupItem)o;
        if (ShowOff.IsChecked != true && !i.Enabled) return false;
        var q = SearchBox.Text.Trim();
        return q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               i.Program.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               i.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var on = _items.Count(i => i.Enabled && !i.Delayed);
        var heavy = _items.Count(i => i.Enabled && !i.Delayed && i.Impact == StartupImpact.High);
        var safe = StartupManager.OptimizeCandidates(_items).Count;
        var suspicious = _items.Count(i => i.IsSuspicious);
        CountText.Text = $"{on} start immediately  •  {heavy} with high impact  •  {safe} safe to turn off" +
                         (suspicious > 0 ? $"  •  ⚠ {suspicious} suspicious" : "");
        UndoOptimizeButton.IsEnabled = StartupManager.CanUndoOptimize();
        OptimizeButton.IsEnabled = safe > 0;
        OptimizeButton.ToolTip = safe > 0 ? $"Turn off the {safe} item(s) Purgely knows are safe to turn off – with undo"
                                          : "Nothing here is known to be safe to turn off";
    }

    private List<StartupItem> Selected()
    {
        var list = ItemsGrid.SelectedItems.Cast<StartupItem>().ToList();
        if (list.Count == 0)
            Dialog.Show(this, "Select one or more items first (Ctrl+click selects several).", "Startup",
                MessageBoxButton.OK, MessageBoxImage.Information);
        return list;
    }

    private async Task ApplyAsync(List<StartupItem> items, Func<StartupItem, (bool Ok, string Message)> action, string busy)
    {
        if (items.Count == 0) return;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = busy;
        var failures = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    var (ok, message) = action(item);
                    if (!ok) failures.Add($"• {item.Name}: {message}");
                }
            });
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        _view?.Refresh();
        UpdateCount();
        if (failures.Count > 0)
            Dialog.Show(this, "Some changes did not work:\n\n" + string.Join("\n", failures.Take(10)), "Startup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        var keep = items.Where(i => i.Safety == StartupSafety.Keep).ToList();
        if (keep.Count > 0 && Dialog.Show(this,
                "Purgely recommends keeping these on:\n\n" + string.Join("\n", keep.Select(i => $"• {i.Name} – {i.Description}")) +
                "\n\nTurn them off anyway?", "Startup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await ApplyAsync(items, StartupManager.Disable, "Turning off…");
    }

    private async void Enable_Click(object sender, RoutedEventArgs e) =>
        await ApplyAsync(Selected(), StartupManager.Enable, "Turning on…");

    private async void Delay_Click(object sender, RoutedEventArgs e) =>
        await ApplyAsync(Selected(), StartupManager.Delay, "Setting a 2-minute delay…");

    private async void Keep_Click(object sender, RoutedEventArgs e) =>
        await ApplyAsync(Selected(), item =>
        {
            var result = item.Enabled ? StartupManager.Disable(item) : (true, "");
            if (result.Item1) StartupManager.SetKeepDisabled(item, true);
            return result;
        }, "Turning off for good…");

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemsGrid.SelectedItem as StartupItem;
        if (item is null) return;
        var path = item.Kind == StartupKind.StartupFolder ? item.ShortcutPath : item.TargetPath;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            Dialog.Show(this, $"The file does not exist:\n\n{path}", "Startup", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        var candidates = StartupManager.OptimizeCandidates(_items);
        if (candidates.Count == 0)
        {
            Dialog.Show(this, "Nothing to optimize – everything that is safe to turn off is already off. 👍", "Optimize",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Dialog.Show(this,
                $"Turn off these {candidates.Count} item(s)? They are all safe to turn off:\n\n" +
                string.Join("\n", candidates.Take(20).Select(i => $"• {i.Name}  ({i.Program})")) +
                (candidates.Count > 20 ? $"\n… and {candidates.Count - 20} more" : "") +
                "\n\nThey stay off even if a program tries to switch itself back on. \"Undo optimize\" turns them all back on.",
                "Optimize", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "Optimizing startup…";
        int count;
        try { count = await Task.Run(() => StartupManager.Optimize(candidates)); }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; }
        _view?.Refresh();
        UpdateCount();
        Dialog.Show(this, $"Turned off {count} item(s). Restart the PC to feel the difference – " +
                              "Purgely will show the before/after startup time at the top of this window.",
            "Optimize", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void UndoOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (Dialog.Show(this, "Turn back on everything the last Optimize turned off?", "Undo optimize",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        BusyOverlay.Visibility = Visibility.Visible;
        BusyTitle.Text = "Turning items back on…";
        int count;
        try { count = await Task.Run(() => StartupManager.UndoOptimize(_items)); }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; }
        _view?.Refresh();
        UpdateCount();
        Dialog.Show(this, $"Turned {count} item(s) back on.", "Undo optimize", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
