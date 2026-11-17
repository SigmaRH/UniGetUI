using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace UniGetUI.PackageEngine.Restrictions;

/// <summary>
/// One allowed manager, source or package. A null <see cref="Source"/> allows every source of the
/// manager and a null <see cref="Id"/> allows every package of the source.
/// </summary>
public sealed record RestrictionRule(string Manager, string? Source, string? Id)
{
    public const char Separator = '|';
    public const string Any = "*";

    public static RestrictionRule? Create(string? manager, string? source, string? id)
    {
        manager = manager?.Trim();
        if (string.IsNullOrEmpty(manager) || manager == Any)
            return null;
        return new RestrictionRule(manager, Normalize(source), Normalize(id));
    }

    /// <summary>
    /// Reads <c>manager|source|id</c>, <c>manager|source</c> or <c>manager</c>. Only the id may
    /// itself contain the separator, so it takes whatever follows the second one.
    /// </summary>
    public static RestrictionRule? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        string[] parts = line.Split(Separator, 3);
        return Create(parts[0], parts.Length > 1 ? parts[1] : null, parts.Length > 2 ? parts[2] : null);
    }

    public string ToLine()
    {
        if (Id is not null)
            return string.Join(Separator, Manager, Source ?? Any, Id);
        return Source is null ? Manager : string.Join(Separator, Manager, Source);
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value == Any ? null : value;
    }
}

public sealed class RestrictionDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("allow")]
    public List<RestrictionEntry?>? Allow { get; set; }
}

/// <summary>
/// A rule as it is written in the file. <see cref="Name"/> and <see cref="Version"/> are there
/// for the administrator reading an exported list and are ignored when the file is applied.
/// </summary>
public sealed class RestrictionEntry
{
    [JsonPropertyName("manager")]
    public string? Manager { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RestrictionDocument))]
public partial class RestrictionJsonContext : JsonSerializerContext
{
    /// <summary>The generated metadata every read and write of a restrictions file goes through.</summary>
    public static JsonTypeInfo<RestrictionDocument> DocumentJsonTypeInfo => Default.RestrictionDocument;
}
