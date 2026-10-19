using System.Globalization;
using Microsoft.Win32;

namespace Purgely.Core;

/// <summary>A registry value stored as text, so it can be saved to JSON and written back later.</summary>
public sealed class RegValue
{
    public string Kind { get; set; } = "";
    public string Data { get; set; } = "";

    /// <summary>False for value types .NET can't write back exactly (REG_LINK, resource lists…).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRoundTrip => Kind is "String" or "ExpandString" or "MultiString" or "Binary" or "DWord" or "QWord" or "None";

    public static RegValue? Read(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return null;
            var kind = key.GetValueKind(name);
            return new RegValue { Kind = kind.ToString(), Data = Encode(value) };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void WriteTo(RegistryKey key, string name)
    {
        if (!CanRoundTrip) throw new InvalidOperationException($"Value type {Kind} can't be written back safely");
        var kind = Enum.Parse<RegistryValueKind>(Kind);
        object data = kind switch
        {
            RegistryValueKind.MultiString => Data.Length == 0 ? Array.Empty<string>() : Data.Split('\0'),
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(Data),
            RegistryValueKind.DWord => unchecked((int)long.Parse(Data, CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => long.Parse(Data, CultureInfo.InvariantCulture),
            _ => Data,
        };
        key.SetValue(name, data, kind);
    }

    public bool SameAs(RegValue? other) => other is not null && other.Kind == Kind && other.Data == Data;

    private static string Encode(object value) => value switch
    {
        string s => s,
        string[] a => string.Join('\0', a),
        byte[] b => Convert.ToBase64String(b),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
