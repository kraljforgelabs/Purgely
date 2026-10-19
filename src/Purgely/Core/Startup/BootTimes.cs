using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Purgely.Core.Startup;

public sealed record BootRecord(DateTime Time, double Seconds);

/// <summary>Reads Windows' own boot-performance log (Diagnostics-Performance).</summary>
public static partial class BootTimes
{
    private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>The most recent boots, newest first.</summary>
    public static List<BootRecord> Recent(int count = 20)
    {
        var result = new List<BootRecord>();
        foreach (var ev in Query(100, count))
        {
            var time = TimeRegex().Match(ev);
            var boot = DataRegex("BootTime").Match(ev);
            if (!time.Success || !boot.Success) continue;
            if (!DateTime.TryParse(time.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var utc)) continue;
            result.Add(new BootRecord(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(),
                double.Parse(boot.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0));
        }
        return result;
    }

    /// <summary>Programs Windows logged as slowing down startup (event 101): file name → count.</summary>
    public static Dictionary<string, int> SlowStartupApps()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in Query(101, 200))
        {
            foreach (Match m in AnyDataRegex().Matches(ev))
            {
                var value = m.Groups[1].Value.Trim();
                if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var file = Path.GetFileName(value);
                counts[file] = counts.GetValueOrDefault(file) + 1;
                break;
            }
        }
        return counts;
    }

    private static IEnumerable<string> Query(int eventId, int count)
    {
        var (code, output) = Shell.Run("wevtutil",
            $"qe {Log} /q:\"*[System[(EventID={eventId})]]\" /c:{count} /rd:true /f:xml", 30_000);
        if (code != 0) return Array.Empty<string>();
        return output.Split("</Event>", StringSplitOptions.RemoveEmptyEntries);
    }

    private static Regex DataRegex(string name) =>
        new($@"<Data Name=['""]{name}['""]>(\d+)</Data>", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"SystemTime=['""]([^'""]+)['""]")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"<Data Name=['""][^'""]*['""]>([^<]*)</Data>")]
    private static partial Regex AnyDataRegex();
}
