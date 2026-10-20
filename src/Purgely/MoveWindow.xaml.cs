using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using Purgely.Core;

namespace Purgely;

/// <summary>Move to a new PC: export the program list here, import it there (winget).</summary>
public partial class MoveWindow : Window
{
    private readonly ObservableCollection<string> _log = new();

    public MoveWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        LogList.ItemsSource = _log;
    }

    private void Log(string line) => Dispatcher.InvokeAsync(() =>
    {
        _log.Add(line);
        if (_log.Count > 2000) _log.RemoveAt(0);
        LogList.ScrollIntoView(line);
    });

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = ImportButton.IsEnabled = !busy;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save my program list",
            FileName = $"My programs ({Environment.MachineName}, {DateTime.Now:yyyy-MM-dd}).json",
            Filter = "Program list (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        _log.Clear();
        SetBusy(true, "Saving the list of your programs…");
        try
        {
            var programs = await Task.Run(ProgramInventory.LoadWithRecentlyRemoved);
            var (exported, missing) = await NewPcTransfer.ExportAsync(dialog.FileName, programs, Log);
            SetBusy(false, $"Saved: {exported} program(s) can be installed automatically" +
                           (missing.Count > 0 ? $", {missing.Count} you'll need to install yourself (see the .txt file next to the list)." : "."));
        }
        catch (Exception ex)
        {
            SetBusy(false, "Could not save the list.");
            Dialog.Show(this, "Could not save the list.\n\n" + ex.Message, "Move to a new PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open a program list", Filter = "Program list (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        List<string> ids;
        try { ids = NewPcTransfer.ReadList(dialog.FileName); }
        catch (Exception ex)
        {
            Dialog.Show(this, "This file is not a Purgely program list.\n\n" + ex.Message, "Move to a new PC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Dialog.Show(this, $"Install {ids.Count} program(s) from this list?\n\n" +
                              string.Join("\n", ids.Take(15).Select(i => "• " + i)) + (ids.Count > 15 ? $"\n• …and {ids.Count - 15} more" : "") +
                              "\n\nPrograms that are already installed are skipped. " +
                              "Everything comes from the publishers' official sources through Windows Package Manager. " +
                              "This can take a while – you can keep using the PC.",
                "Move to a new PC", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _log.Clear();
        SetBusy(true, "Installing your programs… (each one is downloaded, verified and installed)");
        using var busy = Purgely.Core.Activity.Begin();
        try
        {
            var code = await NewPcTransfer.ImportAsync(dialog.FileName, Log);
            SetBusy(false, code == 0 ? "Done – all programs from the list are installed." :
                "Finished – some programs could not be installed (see the log). Install those from their publishers' websites.");
            Timeline.Log(TimelineEventType.Installed, "Program list", "Programs installed from a \"Move to a new PC\" list");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Could not install from the list.");
            Dialog.Show(this, "Could not install from the list.\n\n" + ex.Message, "Move to a new PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
