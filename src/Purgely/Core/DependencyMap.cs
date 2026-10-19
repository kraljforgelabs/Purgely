using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>
/// Dependency Map: finds which installed programs need each runtime
/// (Visual C++ Redistributables and .NET runtimes).
/// </summary>
public static partial class DependencyMap
{
    private static readonly Dictionary<string, string> VcDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vcruntime140.dll"] = "140", ["vcruntime140_1.dll"] = "140", ["msvcp140.dll"] = "140",
        ["msvcp140_1.dll"] = "140", ["msvcp140_2.dll"] = "140", ["concrt140.dll"] = "140",
        ["vccorlib140.dll"] = "140", ["mfc140.dll"] = "140", ["mfc140u.dll"] = "140",
        ["msvcr120.dll"] = "120", ["msvcp120.dll"] = "120", ["mfc120u.dll"] = "120",
        ["msvcr110.dll"] = "110", ["msvcp110.dll"] = "110", ["mfc110u.dll"] = "110",
        ["msvcr100.dll"] = "100", ["msvcp100.dll"] = "100", ["mfc100u.dll"] = "100",
        ["msvcr90.dll"] = "90", ["msvcp90.dll"] = "90",
        ["msvcr80.dll"] = "80", ["msvcp80.dll"] = "80",
    };

    /// <summary>Fills <see cref="InstalledProgram.UsedBy"/> for every runtime in the list.</summary>
    public static void Analyze(IReadOnlyList<InstalledProgram> programs, IProgress<(int Done, int Total)>? progress = null)
    {
        var runtimes = new Dictionary<InstalledProgram, string[]>();
        foreach (var p in programs)
            if (Identify(p) is { } keys) runtimes[p] = keys;
        if (runtimes.Count == 0) return;

        var targets = programs.Where(p => !runtimes.ContainsKey(p) && !p.IsOrphan && !p.IsStoreApp && !p.IsPortable && !p.IsRemovedEarlier).ToList();
        var cache = DependencyCache.Load();
        var results = new ConcurrentDictionary<InstalledProgram, string[]>();
        var done = 0;
        progress?.Report((0, targets.Count));

        Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = 4 }, p =>
        {
            var cacheKey = $"{p.RegistryKey}|{p.Version}|{p.InstallLocation}";
            if (!cache.TryGetValue(cacheKey, out var requirements))
            {
                requirements = RequirementsOf(p).ToArray();
                cache[cacheKey] = requirements;
            }
            results[p] = requirements;
            progress?.Report((Interlocked.Increment(ref done), targets.Count));
        });
        DependencyCache.Save(cache);

        var needs = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, requirements) in results)
        {
            foreach (var key in requirements)
            {
                if (!needs.TryGetValue(key, out var users)) needs[key] = users = new(StringComparer.CurrentCultureIgnoreCase);
                users.Add(p.Name);
            }
        }

        foreach (var (runtime, keys) in runtimes)
        {
            runtime.IsRuntime = true;
            runtime.UsedBy = keys.SelectMany(k => needs.TryGetValue(k, out var u) ? u : Enumerable.Empty<string>())
                .Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>True for Visual C++, .NET and DirectX runtimes – installers often add these legitimately.</summary>
    public static bool IsRuntimeName(string name) =>
        VcRegex().IsMatch(name) || NetRegex().IsMatch(name) ||
        name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WebView2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the requirement keys a runtime satisfies, or null if it is not a runtime.</summary>
    private static string[]? Identify(InstalledProgram p)
    {
        var vc = VcRegex().Match(p.Name);
        if (vc.Success)
        {
            var version = vc.Groups[1].Value.ToLowerInvariant() switch
            {
                "2005" => "80", "2008" => "90", "2010" => "100", "2012" => "110", "2013" => "120",
                _ => "140", // 2015, 2017, 2019, 2022 and the newer "v14" name share one runtime
            };
            var arch = ArchRegex().Match(p.Name) is { Success: true } a
                ? a.Value.ToLowerInvariant() switch { "amd64" => "x64", var v => v }
                : "x86";
            return new[] { $"vc|{version}|{arch}" };
        }

        var net = NetRegex().Match(p.Name);
        if (net.Success)
        {
            var framework = net.Groups[1].Value switch
            {
                var s when s.Contains("Desktop") => "microsoft.windowsdesktop.app",
                var s when s.Contains("ASP") => "microsoft.aspnetcore.app",
                _ => "microsoft.netcore.app",
            };
            return new[] { $"net|{framework}|{net.Groups[2].Value}" };
        }
        return null;
    }

    private static IEnumerable<string> RequirementsOf(InstalledProgram p)
    {
        var folder = ProgramFolder(p);
        if (folder is null) return Array.Empty<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var binaries = Deep(folder, "*.exe").Take(250).Concat(Deep(folder, "*.dll").Take(250));
        foreach (var file in binaries)
        {
            if (PeReader.Read(file) is not { } pe) continue;
            var dir = Path.GetDirectoryName(file)!;
            foreach (var dll in pe.Imports)
            {
                if (!VcDlls.TryGetValue(dll, out var version)) continue;
                if (File.Exists(Path.Combine(dir, dll))) continue; // ships its own copy
                result.Add($"vc|{version}|{pe.Architecture}");
            }
        }

        foreach (var config in Deep(folder, "*.runtimeconfig.json").Take(30))
            foreach (var key in NetRequirements(config))
                result.Add(key);

        return result;
    }

    /// <summary>Files up to 4 folder levels deep (games keep their .exe files deep inside).</summary>
    private static IEnumerable<string> Deep(string folder, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> NetRequirements(string configFile)
    {
        var keys = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configFile));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)) return keys;
            if (options.TryGetProperty("includedFrameworks", out _)) return keys; // self-contained

            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var single)) frameworks.Add(single);
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray());

            foreach (var fw in frameworks)
            {
                var name = fw.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var version = fw.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var major = version.Split('.')[0];
                if (name.Length == 0 || major.Length == 0) continue;
                keys.Add($"net|{name.ToLowerInvariant()}|{major}");
                if (name.Equals("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
                    keys.Add($"net|microsoft.netcore.app|{major}"); // these always sit on top of the base runtime
            }
        }
        catch (Exception) { }
        return keys;
    }

    /// <summary>Best guess of where a program's files live.</summary>
    public static string? ProgramFolder(InstalledProgram p)
    {
        var candidates = new[]
        {
            p.InstallLocation,
            Path.GetDirectoryName(p.DisplayIcon.Split(',')[0].Trim('"', ' ')) ?? "",
            Path.GetDirectoryName(PathUtil.ExtractExecutable(p.UninstallString)) ?? "",
        };
        foreach (var c in candidates)
        {
            if (c.Length <= 3) continue;
            var dir = PathUtil.Normalize(c);
            if (!Directory.Exists(dir)) continue;
            if (PathUtil.SameOrUnder(dir, PathUtil.WindowsDir)) continue;
            if (dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)) continue;
            if (dir.Split('\\').Length < 3) continue; // a drive root or "C:\Program Files" itself
            return dir;
        }
        return null;
    }

    [GeneratedRegex(@"Visual C\+\+ (\d{4}|v14).*(Redistributable|Runtime)", RegexOptions.IgnoreCase)]
    private static partial Regex VcRegex();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchRegex();

    [GeneratedRegex(@"^Microsoft (Windows Desktop Runtime|\.NET Runtime|\.NET Core Runtime|ASP\.NET Core)\D*(\d+)\.\d+", RegexOptions.IgnoreCase)]
    private static partial Regex NetRegex();
}
