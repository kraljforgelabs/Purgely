using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Purgely.Core;

public sealed record WingetUpdate(string Name, string Id, string Version, string Available);

/// <summary>
/// Program updates through winget (Windows Package Manager, built into Windows 11).
/// Purgely only reads the list and starts winget – winget downloads and installs.
/// </summary>
public static partial class WingetUpdates
{
    /// <summary>Why the last check failed, in plain words (shown to the user).</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>
    /// winget is an "app execution alias" in the user's WindowsApps folder; an elevated process does not always
    /// find it through PATH, so look in the known places too.
    /// </summary>
    public static string? FindWinget()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe"),
        };
        try
        {
            var appsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            candidates.AddRange(Directory.EnumerateDirectories(appsRoot, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "winget.exe")));
        }
        catch (Exception) { /* no access to WindowsApps – fine */ }

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Last resort: whatever PATH gives us.
        var where = Shell.Run("where", "winget", 10_000);
        var first = where.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        return where.Code == 0 && first is not null ? first : null;
    }
    /// <summary>Returns the available updates, or null if winget is missing or its output could not be read.</summary>
    public static List<WingetUpdate>? Check()
    {
        LastError = "";
        var winget = FindWinget();
        if (winget is null)
        {
            LastError = "winget (Windows Package Manager) was not found. Install \"App Installer\" from the Microsoft Store.";
            return null;
        }

        string output;
        try
        {
            var psi = new ProcessStartInfo(winget, "upgrade --accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(true); } catch (Exception) { }
                LastError = "winget did not answer within 2 minutes (no internet connection?).";
                return null;
            }
            output = read.Result;
        }
        catch (Exception ex)
        {
            LastError = $"Could not start winget ({winget}): {ex.Message}";
            return null;
        }
        var result = Parse(output);
        if (result is null)
        {
            var snippet = string.Join("\n", output.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(6));
            LastError = $"winget's answer could not be read:\n\n{snippet}";
        }
        return result;
    }

    /// <summary>Reads winget's text table using the column positions of its header line.</summary>
    public static List<WingetUpdate>? Parse(string output)
    {
        // winget draws a spinner with carriage returns – keep only what ends up visible on each line.
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Split('\r').Last().TrimEnd())
            .ToList();

        var dash = lines.FindIndex(l => l.Length > 10 && l.Trim().All(c => c == '-'));
        if (dash < 1) return new List<WingetUpdate>(); // "No installed package found that has an available upgrade"

        var header = lines[dash - 1];
        var nameCol = header.IndexOf("Name", StringComparison.Ordinal);
        var idCol = header.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        var versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        var availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceCol = header.IndexOf("Source", StringComparison.Ordinal);
        if (nameCol < 0 || idCol <= 0 || versionCol < 0 || availableCol < 0) return null; // other language

        string Cut(string line, int from, int to) =>
            from >= line.Length ? "" : line[from..Math.Min(to < 0 ? line.Length : to, line.Length)].Trim();

        var updates = new List<WingetUpdate>();
        foreach (var line in lines.Skip(dash + 1))
        {
            if (line.Trim().Length == 0) break;
            if (line.Length <= availableCol) continue;
            var update = new WingetUpdate(
                Cut(line, nameCol, idCol), Cut(line, idCol, versionCol),
                Cut(line, versionCol, availableCol), Cut(line, availableCol, sourceCol));
            if (update.Id.Length > 0 && update.Available.Length > 0) updates.Add(update);
        }
        return updates;
    }

    // ------------------------------------------------------------------ programs winget could not update
    private static string FailedFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely", "winget-failed.json");

    private static HashSet<string> LoadFailed()
    {
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FailedFile)) ?? new(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Remembers whether winget managed to update a package last time.</summary>
    public static void RecordResult(string wingetId, bool succeeded)
    {
        try
        {
            var failed = LoadFailed();
            if (succeeded) failed.Remove(wingetId); else failed.Add(wingetId);
            Directory.CreateDirectory(Path.GetDirectoryName(FailedFile)!);
            File.WriteAllText(FailedFile, JsonSerializer.Serialize(failed.ToList()));
        }
        catch (Exception) { /* optional */ }
    }

    /// <summary>Connects winget's rows to Purgely's program list (winget may shorten long names with "…").</summary>
    public static int Attach(IReadOnlyList<InstalledProgram> programs, List<WingetUpdate> updates)
    {
        var attached = 0;
        var failed = LoadFailed();
        foreach (var u in updates)
        {
            var name = u.Name.TrimEnd('…', '.').Trim();
            var shortened = u.Name.EndsWith('…');
            var match = programs.FirstOrDefault(p => !p.IsOrphan &&
                (p.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase) ||
                 (shortened && name.Length >= 8 && p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))));
            if (match is null || match.WingetId is not null) continue;
            match.WingetId = u.Id;
            match.AvailableVersion = u.Available;
            match.WingetFailed = failed.Contains(u.Id);
            attached++;
        }
        return attached;
    }

    /// <summary>
    /// Starts winget in its own console window. The window stays open at the end, so the user can read
    /// whether the update worked (and why not, if it failed).
    /// </summary>
    /// <summary>Progress of a winget update, parsed from its output.</summary>
    public sealed record UpgradeProgress(string Stage, double? Percent, string Detail);

    /// <summary>
    /// Runs "winget upgrade" hidden and reports progress (download size, verifying, installing) instead of showing
    /// a console window. Returns winget's exit code and the last lines it printed (for error messages).
    /// </summary>
    public static async Task<(int ExitCode, List<string> Output)> UpgradeAsync(string wingetId, IProgress<UpgradeProgress> progress,
                                                                              CancellationToken cancel = default)
    {
        var winget = FindWinget() ?? "winget";
        var info = new ProcessStartInfo(winget,
            $"upgrade --id \"{wingetId}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        var lines = new List<string>();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget could not be started.");
        progress.Report(new UpgradeProgress("Starting", null, "Asking Windows Package Manager…"));

        async Task Pump(StreamReader reader)
        {
            var buffer = new char[512];
            var current = new System.Text.StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer, cancel)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (current.Length > 0) Handle(current.ToString());
                        current.Clear();
                    }
                    else current.Append(c);
                }
                if (current.Length > 0 && SizeRegex().IsMatch(current.ToString())) Handle(current.ToString()); // live bar
            }
            if (current.Length > 0) Handle(current.ToString());
        }

        void Handle(string raw)
        {
            var line = AnsiRegex().Replace(raw, "").Trim();
            if (line.Length == 0 || line.All(ch => ch is '-' or '\\' or '|' or '/' or ' ')) return; // spinner
            var size = SizeRegex().Match(line);
            if (size.Success)
            {
                var done = ToBytes(size.Groups[1].Value, size.Groups[2].Value);
                var total = ToBytes(size.Groups[3].Value, size.Groups[4].Value);
                progress.Report(new UpgradeProgress("Downloading", total > 0 ? Math.Min(100, done * 100 / total) : null,
                    $"{size.Groups[1].Value} {size.Groups[2].Value} of {size.Groups[3].Value} {size.Groups[4].Value}"));
                return;
            }
            var percent = PercentRegex().Match(line);
            if (percent.Success && !line.Any(char.IsLetter)) // a progress bar like "█████▒▒▒  45%"
            {
                progress.Report(new UpgradeProgress("Working", double.Parse(percent.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), ""));
                return;
            }
            lock (lines) { lines.Add(line); if (lines.Count > 60) lines.RemoveAt(0); }
            if (line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase))
                progress.Report(new UpgradeProgress("Downloading", null, "Downloading the update…"));
            else if (line.Contains("verified", StringComparison.OrdinalIgnoreCase))
                progress.Report(new UpgradeProgress("Verified", null, "Download verified"));
            else if (line.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase))
                progress.Report(new UpgradeProgress("Installing", null, "Installing – this can take a minute…"));
            else if (line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
                progress.Report(new UpgradeProgress("Done", 100, "Installed"));
        }

        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync(cancel));
        return (process.ExitCode, lines);
    }

    private static double ToBytes(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n)) return 0;
        return unit.ToUpperInvariant() switch { "KB" => n * 1024, "MB" => n * 1024 * 1024, "GB" => n * 1024 * 1024 * 1024, _ => n };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"([\d.,]+)\s*(KB|MB|GB|B)\s*/\s*([\d.,]+)\s*(KB|MB|GB|B)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SizeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial System.Text.RegularExpressions.Regex PercentRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial System.Text.RegularExpressions.Regex AnsiRegex();

    public static Process? StartUpgrade(string wingetId)
    {
        var winget = FindWinget() ?? "winget";
        // !errorlevel! (delayed expansion) keeps winget's exit code, so Purgely knows whether it worked.
        var command =
            $"\"\"{winget}\" upgrade --id \"{wingetId}\" --exact --accept-package-agreements --accept-source-agreements" +
            " & set PURGELY_EXIT=!errorlevel!" +
            " & echo. & echo ---------------------------------------------- & echo Purgely: the update has finished." +
            " Read the messages above, then press any key to close this window. & pause >nul & exit !PURGELY_EXIT!\"";
        return Process.Start(new ProcessStartInfo("cmd.exe", $"/v:on /s /c {command}")
        {
            UseShellExecute = true,
        });
    }
}
