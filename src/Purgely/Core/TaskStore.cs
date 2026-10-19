using System.IO;

namespace Purgely.Core;

/// <summary>Scheduled tasks are stored as XML files under C:\Windows\System32\Tasks.</summary>
public static class TaskStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");

    public static IEnumerable<string> AllTaskFiles()
    {
        try
        {
            return Directory.EnumerateFiles(Root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
