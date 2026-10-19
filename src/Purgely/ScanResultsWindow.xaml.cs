using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Purgely.Core;

namespace Purgely;

public partial class ScanResultsWindow : Window
{
    private readonly ObservableCollection<LeftoverItem> _items;
    private readonly string _programName;
    private readonly Action? _onCleaned;

    /// <summary>Size Windows recorded for the program – used to warn when the main folder seems to be missing.</summary>
    private readonly long? _expectedBytes;
    private string _baseWarning = "";

    public ScanResultsWindow(string programName, IEnumerable<LeftoverItem> items, string? warning, Action? onCleaned,
                             string? heading = null, long? expectedBytes = null)
    {
        _expectedBytes = expectedBytes;
        InitializeComponent();
        BusyGuard.Attach(this);
        _programName = programName;
        _onCleaned = onCleaned;
        _items = new ObservableCollection<LeftoverItem>(items);
        ItemsGrid.ItemsSource = _items;
        TitleText.Text = heading ?? $"Leftovers: {programName}";
        Title = $"Purgely – {TitleText.Text}";
        _baseWarning = warning ?? "";
        UpdateSummary();
    }

    /// <summary>Found much less than Windows recorded? Then the program's main folder is probably elsewhere.</summary>
    private string? MissingWarning()
    {
        if (_expectedBytes is not long expected || expected < 200L * 1024 * 1024) return null;
        var found = _items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.File).Sum(i => i.SizeBytes);
        if (found >= expected / 10) return null;
        return $"Windows recorded about {Sizes.Format(expected)} for this program, but Purgely found only " +
               $"{(found > 0 ? Sizes.Format(found) : "a few small items")}. Its main folder is probably somewhere Purgely didn't look " +
               "(e.g. a games folder on another drive). Click \"Add folder…\" to point to it, or it will stay on your disk.";
    }

    private void UpdateWarning()
    {
        var parts = new[] { _baseWarning, MissingWarning() ?? "" }.Where(p => p.Length > 0).ToList();
        WarningText.Text = string.Join("\n\n", parts);
        WarningBox.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The user points to a folder Purgely missed. The same safety rules apply as for anything found.</summary>
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Choose the folder of {_programName}" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = PathUtil.Normalize(dialog.FolderName);
        if (!SafePaths.IsRemovableFolder(folder, out var why))
        {
            Dialog.Show(this, $"Purgely won't remove this folder: {why}.", "Add folder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_items.Any(i => i.Kind == LeftoverKind.Folder && PathUtil.SameOrUnder(folder, i.Location)))
        {
            Dialog.Show(this, "This folder is already in the list.", "Add folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var item = new LeftoverItem { Kind = LeftoverKind.Folder, Location = folder };
        item.AddEvidence(90, "You chose this folder");
        Cursor = System.Windows.Input.Cursors.Wait;
        try { item.SizeBytes = await Task.Run(() => FileMover.SizeOf(folder)); }
        finally { Cursor = null; }
        item.Recommended = true;
        item.IsChecked = true;
        _items.Insert(0, item);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _items.Count(i => i.IsChecked);
        var selectedSize = Sizes.Format(_items.Where(i => i.IsChecked).Sum(i => i.SizeBytes));
        var shared = _items.Count(i => i.IsShared);
        var zombies = _items.Count(i => i.IsZombie && !i.IsShared);
        SummaryText.Text = _items.Count == 0
            ? "No leftovers found – everything is clean."
            : $"{_items.Count} items found  •  {selected} selected" +
              (selectedSize.Length > 0 ? $" ({selectedSize})" : "") + $"  •  {shared} shared with other programs (kept)" +
              (zombies > 0 ? $"  •  {zombies} can bring the program back" : "");
        RemoveButton.Content = $"Remove selected ({selected})";
        UpdateWarning();
        RemoveButton.IsEnabled = selected > 0;
    }

    private void Check_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = i.Recommended;
        UpdateSummary();
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = false;
        UpdateSummary();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0) return;

        var shared = selected.Where(i => i.IsShared).ToList();
        if (shared.Count > 0 && Dialog.Show(this,
                $"{shared.Count} selected item(s) are still used by other installed programs:\n\n" +
                string.Join("\n", shared.Take(5).Select(i => $"• {i.DisplayLocation}\n   ({i.SharedWith})")) +
                "\n\nRemoving them will probably BREAK those programs. Continue anyway?",
                "Shared items", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var missing = MissingWarning();
        if (Dialog.Show(this,
                $"Remove {selected.Count} item(s)?\n\n" +
                (missing is null ? "" : "Note: " + missing + "\n\n") +
                "Everything is saved to an Undo package first – you can put it all back from the Undo center.",
                "Remove leftovers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BusyOverlay.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;
        BusyItem.Text = "Preparing…";
        BusyCount.Text = "";
        var progress = new Progress<(int Done, int Total, string Current)>(p =>
        {
            BusyBar.IsIndeterminate = false;
            BusyBar.Maximum = Math.Max(1, p.Total);
            BusyBar.Value = p.Done;
            BusyCount.Text = $"{p.Done} of {p.Total}";
            BusyItem.Text = p.Current;
        });
        CleanReport report;
        try
        {
            report = await Task.Run(() => Cleaner.Clean(_programName, selected, progress));
        }
        finally
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }

        foreach (var item in selected.Where(i => i.Removed)) _items.Remove(item);
        UpdateSummary();

        var message = $"Removed: {report.Removed}\nFailed: {report.Failures.Count}";
        var details = report.Notes.Concat(report.Failures).Take(10).ToList();
        if (details.Count > 0) message += "\n\n" + string.Join("\n", details.Select(d => $"• {d}"));
        if (report.ScheduledForReboot > 0) message += "\n\n⟳ Restart the PC to finish removing locked items.";
        if (report.Removed > 0) message += "\n\nYou can undo this in the Undo center. The disk space is freed when you Discard the package there.";
        Dialog.Show(this, message, "Done", MessageBoxButton.OK,
            report.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        _onCleaned?.Invoke();
    }
}
