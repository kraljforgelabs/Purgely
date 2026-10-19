using System.IO;
using System.Text.Json;

namespace Purgely.Core;

/// <summary>
/// Purgely's open knowledge base: the JSON files in src/Purgely/Knowledge, built into the program.
/// Anyone can suggest a change on GitHub; every change is reviewed (council) before it ships.
/// There is deliberately NO way to load rules from disk at run time – a planted file must never be able to
/// tell an administrator-level cleaner what to delete.
/// </summary>
public static class KnowledgeBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static T Load<T>(string fileName) where T : new()
    {
        var assembly = typeof(KnowledgeBase).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) throw new FileNotFoundException($"Built-in knowledge file '{fileName}' is missing.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
    }
}
