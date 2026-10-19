using System.Collections.ObjectModel;
using System.Windows;
using Purgely.Core;

namespace Purgely;

/// <summary>Driver updates from Windows Update: check, choose, install – with per-driver status.</summary>
public partial class DriverWindow : Window
{
    private ObservableCollection<DriverUpdate> _drivers = new();

    public DriverWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        DriversGrid.ItemsSource = _drivers;
        Loaded += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        BusyPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        BusyText.Text = "Asking Windows Update which drivers are newer…";
        RecheckButton.IsEnabled = InstallButton.IsEnabled = false;
        SummaryText.Text = "Asking Windows Update…";
        var found = await Task.Run(DriverUpdates.Check);
        BusyPanel.Visibility = Visibility.Collapsed;
        RecheckButton.IsEnabled = true;

        if (found is null)
        {
            _drivers = new();
            DriversGrid.ItemsSource = _drivers;
            EmptyText.Text = $"Windows Update could not be reached.\n\n{DriverUpdates.LastError}";
            EmptyPanel.Visibility = Visibility.Visible;
            SummaryText.Text = "Check your internet connection and try again.";
            return;
        }
        // Optional drivers (Windows doesn't install them on its own) are offered unticked.
        foreach (var d in found) d.IsChecked = !d.Optional;
        _drivers = new ObservableCollection<DriverUpdate>(found);
        DriversGrid.ItemsSource = _drivers;
        EmptyPanel.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "All drivers are up to date – Windows Update has nothing newer for this PC.";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var chosen = _drivers.Count(d => d.IsChecked);
        SummaryText.Text = _drivers.Count == 0
            ? "Everything is up to date."
            : $"{_drivers.Count} driver update(s) available  •  {_drivers.Count(d => d.Optional)} optional  •  {chosen} selected";
        InstallButton.Content = $"Install selected ({chosen})";
        InstallButton.IsEnabled = chosen > 0;
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private async void Check_Again_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _drivers.Where(d => d.IsChecked).ToList();
        if (chosen.Count == 0) return;
        if (Dialog.Show(this,
                $"Install {chosen.Count} driver update(s) from Windows Update?\n\n" +
                string.Join("\n", chosen.Take(12).Select(d => $"• {d.Title}")) +
                "\n\nThe screen may flicker or the network may drop for a moment while a driver is replaced. " +
                "Save your work first. A restart may be needed.",
                "Driver updates", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        RecheckButton.IsEnabled = InstallButton.IsEnabled = false;
        DriversGrid.IsHitTestVisible = false; DriversGrid.Opacity = 0.45;
        foreach (var d in chosen) d.Status = "Waiting…";
        var progress = new Progress<(int Index, string Status)>(p =>
        {
            chosen[p.Index].Status = p.Status;
            SummaryText.Text = $"Installing {p.Index + 1} of {chosen.Count}: {chosen[p.Index].Title}";
        });
        bool reboot;
        try { reboot = await Task.Run(() => DriverUpdates.Install(chosen, progress)); }
        catch (Exception ex)
        {
            Dialog.Show(this, $"Windows Update could not install the drivers:\n\n{ex.Message}", "Driver updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            reboot = false;
        }
        DriversGrid.IsHitTestVisible = true; DriversGrid.Opacity = 1;
        RecheckButton.IsEnabled = true;
        var ok = chosen.Count(d => d.Status.StartsWith("Installed"));
        SummaryText.Text = $"{ok} of {chosen.Count} driver(s) installed" + (reboot ? "  •  restart the PC to finish" : "");
        if (reboot)
            Dialog.Show(this, "Restart the PC to finish installing the drivers.", "Driver updates",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
