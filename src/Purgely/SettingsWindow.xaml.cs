using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Purgely.Core;

namespace Purgely;

/// <summary>Settings: update check, Timeline history, Undo backups, help links and "Remove Purgely".</summary>
public partial class SettingsWindow : Window
{
    public const string RepoUrl = "https://github.com/kraljforgelabs/Purgely";

    /// <summary>Set when the user asked for the welcome tour – the home screen shows it when this page closes.</summary>
    public bool WelcomeRequested { get; private set; }

    private PurgelyRelease? _release;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        BusyGuard.Attach(this);
        var settings = AppSettings.Current;
        UpdatesBox.IsChecked = settings.CheckForUpdates;
        var keep = settings.TimelineKeepDays.ToString();
        TimelineBox.SelectedItem = TimelineBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep)
                                   ?? TimelineBox.Items[0];
        VersionTitle.Text = $"Purgely {UpdateCheck.CurrentVersion}" + (UpdateCheck.CurrentVersion.Major < 1 ? " (beta)" : "");
        UpdateStatus.Text = settings.LastUpdateCheck is { } last
            ? $"Last checked {last:yyyy-MM-dd HH:mm}." + (UpdateCheck.Parse(settings.AvailableTag) is { } newer && newer > UpdateCheck.CurrentVersion
                ? $" Version {newer} is available." : " No newer version was found.")
            : "Not checked yet.";
        if (settings.AvailableTag.Length > 0 && UpdateCheck.Parse(settings.AvailableTag) is { } v && v > UpdateCheck.CurrentVersion)
        {
            _release = new PurgelyRelease(v, settings.AvailableTag, UpdateCheck.IsReleaseUrl(settings.AvailableUrl) ? settings.AvailableUrl : UpdateCheck.ReleasesPage, false);
            DownloadButton.Visibility = Visibility.Visible;
        }
        _loading = false;
        Loaded += async (_, _) => await CountUndoAsync();
    }

    private async Task CountUndoAsync()
    {
        try
        {
            var (count, bytes, otherDrives) = await Task.Run(() =>
            {
                var open = UndoStore.List().Where(e => !e.Manifest.Restored).ToList();
                var drives = DataFolders.DriveUndoFolders();
                var size = (Directory.Exists(DataFolders.Shared) ? FileMover.SizeOf(DataFolders.Shared) : 0) +
                           drives.Sum(f => FileMover.SizeOf(f));
                return (open.Count, size, drives.Count);
            });
            UndoStatus.Text = (count == 0 ? "No backups waiting." : $"{count} cleanup(s) can be undone.") +
                              $" Purgely's data uses {Sizes.Format(bytes)} in {DataFolders.Shared}" +
                              (otherDrives > 0 ? " and \"Purgely Undo\" on other drives." : ".") +
                              " Discard old backups in the Undo center to free space.";
            if (!DataFolders.IsAdministrator())
                ProtectStatus.Text = "Purgely is not running as administrator, so it can't check the protection of its data folder now.";
        }
        catch (Exception) { UndoStatus.Text = "Couldn't count the backups."; }
    }

    // ------------------------------------------------------------------ updates
    private void UpdatesBox_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CheckForUpdates = UpdatesBox.IsChecked == true;
        AppSettings.Save();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateStatus.Text = "Asking GitHub…";
        try
        {
            _release = await UpdateCheck.FindNewerAsync();
            var settings = AppSettings.Current;
            settings.LastUpdateCheck = DateTime.Now;
            settings.AvailableTag = _release?.Tag ?? "";
            settings.AvailableUrl = _release?.PageUrl ?? "";
            AppSettings.Save();
            UpdateStatus.Text = _release is null
                ? $"No newer version was found (checked {DateTime.Now:HH:mm}). If you are offline, Purgely can't tell."
                : $"Version {_release.Version} is available{(_release.IsPreview ? " (beta)" : "")}. Download it from the release page and replace your Purgely.exe.";
            DownloadButton.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { CheckNowButton.IsEnabled = true; }
    }

    private void Download_Click(object sender, RoutedEventArgs e) => UpdateCheck.OpenReleasePage(_release?.PageUrl);

    // ------------------------------------------------------------------ history
    private void TimelineBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TimelineBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var days)) return;
        AppSettings.Current.TimelineKeepDays = days;
        AppSettings.Save();
        if (days > 0) _ = Task.Run(() => Timeline.ClearHistory(days));
    }

    private void OpenUndo_Click(object sender, RoutedEventArgs e) => PageNav.Open(this, new UndoWindow(), () => _ = CountUndoAsync());

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(DataFolders.Shared)) Open(DataFolders.Shared);
    }

    // ------------------------------------------------------------------ help
    private void Welcome_Click(object sender, RoutedEventArgs e)
    {
        WelcomeRequested = true;
        Close();
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => Open($"{RepoUrl}/blob/main/docs/Purgely-User-Guide.pdf");
    private void Report_Click(object sender, RoutedEventArgs e) => Open($"{RepoUrl}/issues/new/choose");
    private void Privacy_Click(object sender, RoutedEventArgs e) => Open($"{RepoUrl}/blob/main/PRIVACY.md");
    private void Source_Click(object sender, RoutedEventArgs e) => Open(RepoUrl);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ remove Purgely
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Core.Activity.IsBusy)
        {
            Dialog.Show(this, "Purgely is still changing your PC.\n\nWait until it has finished, then try again.", "Remove Purgely",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RemoveButton.IsEnabled = false;
        try
        {
            var plan = await Task.Run(SelfRemoval.Plan);
            var lines = new List<string>
            {
                $"Purgely's backups, history and settings will be deleted ({Sizes.Format(plan.Bytes)}):",
            };
            lines.AddRange(plan.Folders.Select(f => "   • " + f));
            if (plan.UndoPackages > 0)
                lines.Add($"\n⚠ {plan.UndoPackages} cleanup(s) can still be undone. After this, what Purgely removed can't be brought back. " +
                          "If you want anything back, press Cancel and restore it in the Undo center first.");
            if (plan.OpenTrials > 0)
                lines.Add($"\n⚠ {plan.OpenTrials} program(s) are still on trial (Install & Track). Their records are deleted too, so Purgely can no longer revert them.");
            if (plan.DelayedStartupItems > 0)
                lines.Add($"\n{plan.DelayedStartupItems} startup item(s) you delayed will keep starting 2 minutes after sign-in (Task Scheduler → Purgely → Delayed).");
            lines.Add("\nYour own files and programs are not touched. Ads, privacy and startup settings stay as you set them.");
            if (plan.ExePath is null)
                lines.Add("\nPurgely.exe itself stays – delete it yourself when Purgely is closed.");

            var labels = new Dictionary<MessageBoxResult, string>
            {
                [MessageBoxResult.Yes] = plan.ExePath is null ? "Remove data" : "Remove data and Purgely.exe",
                [MessageBoxResult.No] = "Remove data only",
                [MessageBoxResult.Cancel] = "Cancel",
            };
            var answer = Dialog.Show(this, "Remove Purgely from this PC?\n\n" + string.Join("\n", lines), "Remove Purgely",
                plan.ExePath is null ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
                plan.ExePath is null ? MessageBoxResult.No : MessageBoxResult.Cancel,
                plan.ExePath is null
                    ? new Dictionary<MessageBoxResult, string> { [MessageBoxResult.Yes] = "Remove data", [MessageBoxResult.No] = "Cancel" }
                    : labels);
            var removeExe = answer == MessageBoxResult.Yes && plan.ExePath is not null;
            var removeData = answer == MessageBoxResult.Yes || (answer == MessageBoxResult.No && plan.ExePath is not null);
            if (!removeData) return;

            int scheduled;
            using (Core.Activity.Begin()) scheduled = await Task.Run(() => SelfRemoval.RemoveData(plan));
            var exeNote = removeExe && SelfRemoval.ScheduleExeDelete(plan.ExePath!) ? " Purgely.exe will be deleted a few seconds after Purgely closes." : "";
            Dialog.Show(this, "Purgely's data has been removed.\n\n" +
                              (scheduled > 0 ? $"{scheduled} file(s) were in use and will be removed when you restart the PC. " : "") +
                              exeNote + " Thank you for trying Purgely!", "Remove Purgely",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PageNav.Exiting = true; // nothing may reopen a window or write Purgely's data again
            Application.Current.Shutdown();
        }
        finally
        {
            if (IsLoaded) RemoveButton.IsEnabled = true;
        }
    }
}
