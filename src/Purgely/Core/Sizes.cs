using System.IO;

namespace Purgely.Core;

public static class Sizes
{
    public static long Of(LeftoverItem item)
    {
        try
        {
            return item.Kind switch
            {
                LeftoverKind.Folder when Directory.Exists(item.Location) =>
                    new DirectoryInfo(item.Location)
                        .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Sum(f => f.Length),
                LeftoverKind.File or LeftoverKind.Shortcut when File.Exists(item.Location) =>
                    new FileInfo(item.Location).Length,
                _ => 0,
            };
        }
        catch (Exception) { return 0; }
    }

    public static string Format(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024d:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.00} GB",
    };
}
