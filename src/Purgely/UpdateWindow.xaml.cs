using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Purgely.Core;

namespace Purgely;

/// <summary>
/// Updates one program through winget without a console window: download size, verification and install
/// are shown as steps. If a silent update fails, it offers to run the installer visibly.
/// </summary>
public partial class UpdateWindow : Window
{
    private const string Done = "", Running = "", Failed = "", Pending = "";

    private readonly IReadOnlyList<InstalledProgram> _queue;
    private InstalledProgram _program;
    private DateTime _itemStarted = DateTime.Now;
    private readonly List<(InstalledProgram Program, bool Ok, string Why)> _results = new();
    private readonly List<string> _allOutput = new();
    private readonly DateTime _started = DateTime.Now;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _finished;

    /// <summary>True when winget reported success.</summary>
    public bool Succeeded { get; private set; }

    public UpdateWindow(InstalledProgram program) : this(new[] { program }) { }

    /// <summary>Updates several programs one after another ("Update all").</summary>
    public UpdateWindow(IReadOnlyList<InstalledProgram> programs)
    {
        InitializeComponent();
        _queue = programs;
        _program = programs[0];
        _clock.Tick += (_, _) => ElapsedText.Text = Format(DateTime.Now - _started);
        Loaded += async (_, _) => { _clock.Start(); await RunAsync(); };
        Closed += (_, _) => _clock.Stop();
    }

    private async Task RunAsync()
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            _program = _queue[i];
            _itemStarted = DateTime.Now;
            ResetSteps();
            TitleText.Text = _queue.Count == 1 ? $"Updating {_program.Name}" : $"Updating {i + 1} of {_queue.Count}: {_program.Name}";
            VersionText.Text = $"{_program.Version}  →  {_program.AvailableVersion}   ·   from the official source, via Windows Package Manager";

            var progress = new Progress<WingetUpdates.UpgradeProgress>(ShowProgress);
            int exit;
            List<string> output;
            try
            {
                (exit, output) = await WingetUpdates.UpgradeAsync(_program.WingetId!, progress);
            }
            catch (Exception ex)
            {
                exit = -1;
                output = new List<string> { ex.Message };
            }
            WingetUpdates.RecordResult(_program.WingetId!, exit == 0);
            _results.Add((_program, exit == 0, exit == 0 ? "" : Explain(exit, output)));
            _allOutput.Add($"===== {_program.Name} (exit code {exit}) =====");
            _allOutput.AddRange(output);
            if (_queue.Count == 1)
            {
                Finish(exit == 0, exit == 0 ? "" : Explain(exit, output), output);
                return;
            }
        }
        FinishAll();
    }

    private void ResetSteps()
    {
        _finished = false;
        SetStep(1, Running, "Download the update");
        SetStep(2, Pending, "Check that the download is genuine");
        SetStep(3, Pending, "Install it");
        Step2Text.Opacity = Step3Text.Opacity = 0.7;
        ProgressLine.IsIndeterminate = true;
        StatusText.Text = "Asking Windows Package Manager…";
    }

    /// <summary>Summary after "Update all": how many worked, and why the others didn't.</summary>
    private void FinishAll()
    {
        _finished = true;
        _clock.Stop();
        var ok = _results.Count(r => r.Ok);
        var failed = _results.Where(r => !r.Ok).ToList();
        Succeeded = failed.Count == 0;
        TitleText.Text = failed.Count == 0 ? $"All {ok} programs updated" : $"{ok} of {_results.Count} programs updated";
        VersionText.Text = $"Took {Format(DateTime.Now - _started)}";
        ProgressLine.IsIndeterminate = false;
        ProgressLine.Value = 100;
        foreach (var step in new[] { 1, 2, 3 }) SetStep(step, Done, StepText(step).Text);
        StatusText.Text = "";
        CloseButton.Content = "Close";
        DetailsBox.Text = string.Join(Environment.NewLine, _allOutput);
        DetailsExpander.Visibility = Visibility.Visible;
        ResultBar.Visibility = Visibility.Visible;
        if (failed.Count == 0)
        {
            ResultBar.Style = (Style)FindResource("InfoBarSuccess");
            ResultIcon.Style = (Style)FindResource("InfoBarIconSuccess");
            ResultText.Text = "Done. Programs that were open need a restart to use the new version.";
        }
        else
        {
            ResultText.Text = "Not updated:\n" + string.Join("\n", failed.Select(f => $"• {f.Program.Name} – {f.Why}")) +
                              "\n\nYou can update these one at a time with the installer visible.";
        }
    }

    private void ShowProgress(WingetUpdates.UpgradeProgress p)
    {
        if (_finished) return;
        switch (p.Stage)
        {
            case "Downloading":
                SetStep(1, Running, "Downloading the update");
                break;
            case "Verified":
                SetStep(1, Done, "Downloaded");
                SetStep(2, Done, "The download is genuine (checked against its published fingerprint)");
                break;
            case "Installing":
            case "Working":
                SetStep(1, Done, "Downloaded");
                SetStep(2, Done, "The download is genuine");
                SetStep(3, Running, "Installing…");
                break;
        }
        if (p.Percent is { } percent)
        {
            ProgressLine.IsIndeterminate = false;
            ProgressLine.Value = percent;
        }
        else if (p.Stage is "Installing") ProgressLine.IsIndeterminate = true;
        if (p.Detail.Length > 0) StatusText.Text = p.Detail;
    }

    private void Finish(bool ok, string problem, List<string> output)
    {
        _finished = true;
        _clock.Stop();
        Succeeded = ok;
        ProgressLine.IsIndeterminate = false;
        ProgressLine.Value = ok ? 100 : 0;
        CloseButton.Content = "Close";
        DetailsBox.Text = string.Join(Environment.NewLine, output);
        DetailsExpander.Visibility = output.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultBar.Visibility = Visibility.Visible;
        if (ok)
        {
            SetStep(1, Done, "Downloaded");
            SetStep(2, Done, "The download is genuine");
            SetStep(3, Done, "Installed");
            StatusText.Text = $"Updated to {_program.AvailableVersion} in {Format(DateTime.Now - _itemStarted)}.";
            ResultBar.Style = (Style)FindResource("InfoBarSuccess");
            ResultIcon.Style = (Style)FindResource("InfoBarIconSuccess");
            ResultText.Text = "Done. If the program was open, restart it to use the new version.";
        }
        else
        {
            StatusText.Text = "The update did not finish.";
            ResultText.Text = problem;
            VisibleButton.Visibility = Visibility.Visible;
            foreach (var step in new[] { 1, 2, 3 })
                if (StepIcon(step).Text == Running) SetStep(step, Failed, StepText(step).Text);
        }
    }

    /// <summary>Plain-language reasons for the most common winget results.</summary>
    private static string Explain(int exit, List<string> output)
    {
        var text = string.Join(" ", output);
        if (text.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No newer package", StringComparison.OrdinalIgnoreCase))
            return "Windows Package Manager found no update it can install for this program. Some programs update only through their own updater.";
        if (text.Contains("in use", StringComparison.OrdinalIgnoreCase) || text.Contains("close", StringComparison.OrdinalIgnoreCase))
            return "The program seems to be running. Close it and try again.";
        if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return "The installer was cancelled.";
        if (text.Contains("hash", StringComparison.OrdinalIgnoreCase) && text.Contains("not match", StringComparison.OrdinalIgnoreCase))
            return "The download did not match its published fingerprint, so it was NOT installed (this protects you from tampered files). Try again later.";
        return $"The installer reported a problem (code {exit}). Some installers need to show their own window – try again with the installer visible. The details are below.";
    }

    private TextBlock StepIcon(int step) => step switch { 1 => Step1Icon, 2 => Step2Icon, _ => Step3Icon };
    private TextBlock StepText(int step) => step switch { 1 => Step1Text, 2 => Step2Text, _ => Step3Text };

    private void SetStep(int step, string icon, string text)
    {
        var i = StepIcon(step);
        var t = StepText(step);
        i.Text = icon;
        i.Opacity = icon == Pending ? 0.5 : 1;
        i.Foreground = (Brush)FindResource(icon == Done ? "OkBrush" : icon == Failed ? "AttentionBrush" : "AccentBrush");
        t.Text = text;
        t.Opacity = 1;
    }

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.Seconds} s";

    private async void Visible_Click(object sender, RoutedEventArgs e)
    {
        VisibleButton.IsEnabled = false;
        StatusText.Text = "Running the update with the installer visible – answer its questions if it asks…";
        ProgressLine.IsIndeterminate = true;
        try
        {
            using var process = WingetUpdates.StartUpgrade(_program.WingetId!);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                WingetUpdates.RecordResult(_program.WingetId!, process.ExitCode == 0);
                Succeeded = process.ExitCode == 0;
            }
        }
        catch (Exception ex) { StatusText.Text = $"Could not start: {ex.Message}"; }
        ProgressLine.IsIndeterminate = false;
        if (Succeeded)
        {
            StatusText.Text = "Updated.";
            ResultBar.Visibility = Visibility.Collapsed;
        }
        VisibleButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
