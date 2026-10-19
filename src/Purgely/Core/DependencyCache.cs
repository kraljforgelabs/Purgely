using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>Remembers Dependency Map results so the next start is almost instant.</summary>
public static class DependencyCache
{
    private const int FormatVersion = 1;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgely", "dependency-cache.json");

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, string[]> Entries { get; set; } = new();
    }

    public static ConcurrentDictionary<string, string[]> Load()
    {
        try
        {
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath));
            if (file?.Version == FormatVersion)
                return new ConcurrentDictionary<string, string[]>(file.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { /* missing or damaged cache – start fresh */ }
        return new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }

    public static void Save(ConcurrentDictionary<string, string[]> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var file = new CacheFile { Version = FormatVersion, Entries = new Dictionary<string, string[]>(entries) };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file));
        }
        catch (Exception) { /* the cache is optional */ }
    }
}
