using System.IO;
using System.Text.RegularExpressions;

namespace Purgely.Core;

public static partial class PathUtil
{
    public static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\'); }
        catch (Exception) { return path.Trim().Trim('"').TrimEnd('\\'); }
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string path, string folder) =>
        path.Length > 0 && folder.Length > 3 &&
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool SameOrUnder(string path, string folder) =>
        string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsUnder(path, folder);

    /// <summary>Gets the executable from a command line, service ImagePath or task command.</summary>
    public static string ExtractExecutable(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith(@"\??\")) command = command[4..];
        if (command.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            command = WindowsDir + command[11..];
        if (command.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            command = Path.Combine(WindowsDir, command);
        if (command.Length == 0) return "";

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        var m = ExecutableRegex().Match(command);
        if (m.Success) return m.Groups[1].Value;
        // Unquoted, with spaces and no known extension: we can't tell where the path ends – "unknown".
        return command.Contains(' ') ? "" : command;
    }

    public static IEnumerable<string> Dirs(string path)
    {
        try { return Directory.EnumerateDirectories(path, "*", Options).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public static IEnumerable<string> Files(string path, string pattern = "*")
    {
        try { return Directory.EnumerateFiles(path, pattern, Options).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    [GeneratedRegex(@"^(.+?\.(exe|sys|dll|bat|cmd))(?=[""\s,]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableRegex();
}
