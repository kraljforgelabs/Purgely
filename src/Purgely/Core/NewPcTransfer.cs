using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>
/// "Move to a new PC": saves the list of your programs (winget export) and installs them again on another PC
/// (winget import). Programs winget doesn't know are written to a separate list so nothing is forgotten.
/// </summary>
public static class NewPcTransfer
{
    /// <summary>Exports to <paramref name="file"/>. Returns (programs in the list, programs winget doesn't know).</summary>
    public static async Task<(int Exported, List<string> Missing)> ExportAsync(string file, IReadOnlyList<InstalledProgram> programs,
                                                                             Action<string> log)
    {
        var missing = new List<string>();
        var code = await RunAsync($"export -o \"{file}\" --accept-source-agreements --disable-interactivity", line =>
        {
            log(line);
            const string marker = "Installed package is not available from any source:";
            var i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) missing.Add(line[(i + marker.Length)..].Trim());
        });
        if (!File.Exists(file)) throw new InvalidOperationException($"winget could not save the list (exit code {code}).");

        var exported = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
                exported += source.GetProperty("Packages").GetArrayLength();
        }
        catch (Exception) { }

        // Programs winget can't reinstall: keep a readable list next to it (Store apps and portable folders included).
        var extra = missing.Concat(programs.Where(p => p.IsPortable).Select(p => $"{p.Name} (portable folder: {p.InstallLocation})"))
                           .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        File.WriteAllLines(Path.ChangeExtension(file, null) + " - install these yourself.txt",
            new[] { "Programs Purgely couldn't add to the list (install them from the publisher's website).",
                    "Note: if Windows uses a language other than English, winget's messages can't be read – in that case, compare with the list yourself.", "" }.Concat(extra.Select(n => "• " + n)));
        return (exported, extra);
    }

    /// <summary>The package ids in a saved list (to show the user what will be installed).</summary>
    public static List<string> ReadList(string file)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
            foreach (var package in source.GetProperty("Packages").EnumerateArray())
                if (package.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s);
        return ids;
    }

    /// <summary>Installs everything in the list that isn't installed yet. Returns winget's exit code.</summary>
    public static Task<int> ImportAsync(string file, Action<string> log) =>
        RunAsync($"import -i \"{file}\" --accept-package-agreements --accept-source-agreements --ignore-unavailable --disable-interactivity", log);

    private static async Task<int> RunAsync(string args, Action<string> log)
    {
        var winget = WingetUpdates.FindWinget() ?? throw new InvalidOperationException(
            "winget (App Installer) was not found. Install \"App Installer\" from the Microsoft Store first.");
        var info = new ProcessStartInfo(winget, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget could not be started.");
        async Task Pump(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                var clean = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-9;?]*[A-Za-z]", "").Trim();
                // Skip spinners and progress bars – the log should read like sentences.
                if (clean.Length < 3 || clean.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒') ||
                    System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[█▒\s]*\d{1,3}\s*%$|^[\d.,]+\s*(KB|MB|GB)\s*/"))
                    continue;
                log(clean);
            }
        }
        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync());
        return process.ExitCode;
    }
}
