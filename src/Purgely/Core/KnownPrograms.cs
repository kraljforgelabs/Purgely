using System.Text.RegularExpressions;

namespace Purgely.Core;

/// <summary>
/// Hand-checked knowledge about popular programs that leave things in places the name search can't find
/// (e.g. OneDrive under %LOCALAPPDATA%\Microsoft). Program parts are offered and may be ticked; user data
/// (settings, saves, caches with logins) is offered but never ticked automatically.
/// Never lists folders holding the user's own files (the OneDrive folder itself, Documents, game saves on purpose).
/// </summary>
public static class KnownPrograms
{
    public sealed record Rule(string Program, Regex Name, string[] ProgramPaths, string[] UserDataPaths);

    private sealed class RulesFile
    {
        public List<RuleEntry> Rules { get; set; } = new();
    }

    private sealed class RuleEntry
    {
        public string Program { get; set; } = "";
        public string Name { get; set; } = "";
        public string[] ProgramPaths { get; set; } = Array.Empty<string>();
        public string[] UserDataPaths { get; set; } = Array.Empty<string>();
    }

    /// <summary>Knowledge/known-programs.json.</summary>
    private static readonly Lazy<Rule[]> RulesLazy = new(() =>
        KnowledgeBase.Load<RulesFile>("known-programs.json").Rules
            .Where(r => r.Program.Length > 0 && r.Name.Length > 0)
            .Select(r => new Rule(r.Program,
                new Regex(r.Name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
                r.ProgramPaths, r.UserDataPaths))
            .ToArray());

    public static Rule[] Rules => RulesLazy.Value;

    /// <summary>Paths that must never come from a rule, even by mistake.</summary>
    private static readonly string[] NeverRoots = { "%USERPROFILE%\\OneDrive", "%USERPROFILE%\\Dropbox" };

    public static Rule? For(string programName) =>
        Rules.FirstOrDefault(r => r.Name.IsMatch(programName.Trim()));

    /// <summary>Expands %VARS% for the current user; empty when a variable is unknown or the result is user content.</summary>
    public static string Expand(string path)
    {
        if (path.StartsWith("HK", StringComparison.OrdinalIgnoreCase)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.Contains('%')) return "";
        foreach (var never in NeverRoots)
        {
            var n = Environment.ExpandEnvironmentVariables(never);
            if (PathUtil.SameOrUnder(PathUtil.Normalize(expanded), PathUtil.Normalize(n))) return "";
        }
        return expanded;
    }
}
