using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Purgely.Core;

namespace Purgely;

public partial class UndoWindow : Window
{
    public UndoWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        Reload();
    }

    private void Reload()
    {
        var list = UndoStore.List();
        PackagesGrid.ItemsSource = list;
        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UndoEntry? Selected()
    {
        if (PackagesGrid.SelectedItems.Count > 1)
        {
            Dialog.Show(this, "Restore works on one package at a time – select just one.", "Undo center",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        if (PackagesGrid.SelectedItem is UndoEntry entry) return entry;
        Dialog.Show(this, "Select a package in the list first.", "Undo center",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } entry) return;
        if (entry.Manifest.Restored &&
            Dialog.Show(this, "This package has already been restored. Restore it again?", "Undo center",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (Dialog.Show(this, $"Put back everything removed for \"{entry.Title}\" ({entry.Count} items)?",
                "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Cursor = Cursors.Wait;
        PackagesGrid.IsHitTestVisible = false; // not IsEnabled: a disabled Fluent DataGrid turns white
        PackagesGrid.Opacity = 0.45;
        IsHitTestVisible = false;
        List<string> notes;
        try { notes = await Task.Run(() => UndoStore.Restore(entry)); }
        finally { Cursor = null; IsHitTestVisible = true; PackagesGrid.IsHitTestVisible = true; PackagesGrid.Opacity = 1; }

        Dialog.Show(this, notes.Count == 0
                ? "Everything was restored."
                : "Restored, with notes:\n\n" + string.Join("\n", notes.Take(10).Select(n => $"• {n}")),
            "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    /// <summary>Discards every selected package at once (Ctrl/Shift-click or Ctrl+A to select several).</summary>
    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        var entries = PackagesGrid.SelectedItems.OfType<UndoEntry>().ToList();
        if (entries.Count == 0)
        {
            Dialog.Show(this, "Select one or more packages in the list first (Ctrl or Shift for several, Ctrl+A for all).",
                "Undo center", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var notRestored = entries.Count(x => !x.Manifest.Restored);
        if (Dialog.Show(this, (entries.Count == 1
                    ? $"Permanently delete the Undo package for \"{entries[0].Title}\"?"
                    : $"Permanently delete {entries.Count} Undo packages?\n\n" +
                      string.Join("\n", entries.Take(12).Select(x => $"• {x.CreatedText}  {x.Title}")) +
                      (entries.Count > 12 ? $"\n• …and {entries.Count - 12} more" : "")) +
                  "\n\nAfter this, " + (entries.Count == 1 ? "this cleanup" : "these cleanups") + " can no longer be undone." +
                  (notRestored > 0 ? $"\n\n{notRestored} of them were never restored – make sure everything works without them." : ""),
                "Discard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        Cursor = Cursors.Wait;
        PackagesGrid.IsHitTestVisible = false;
        PackagesGrid.Opacity = 0.45;
        try { await Task.Run(() => { foreach (var entry in entries) UndoStore.Discard(entry); }); }
        catch (Exception ex)
        {
            Dialog.Show(this, "Some packages could not be deleted completely.\n\n" + ex.Message, "Discard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Cursor = null; PackagesGrid.IsHitTestVisible = true; PackagesGrid.Opacity = 1; }
        Reload();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var folder = (PackagesGrid.SelectedItem as UndoEntry)?.Folder ?? Cleaner.UndoRoot;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
