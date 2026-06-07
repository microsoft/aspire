// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestSelector.Models;

/// <summary>
/// Root configuration for the test selector.
/// </summary>
public sealed class TestSelectorConfig
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Glob patterns for files that should be completely ignored (no test triggers).
    /// </summary>
    [JsonPropertyName("ignorePaths")]
    public List<string> IgnorePaths { get; set; } = [];

    /// <summary>
    /// Glob patterns for critical files - if any match, ALL tests run.
    /// </summary>
    [JsonPropertyName("triggerAllPaths")]
    public List<string> TriggerAllPaths { get; set; } = [];

    /// <summary>
    /// Patterns to identify test projects from affected project paths.
    /// </summary>
    [JsonPropertyName("testProjectPatterns")]
    public IncludeExcludePatterns TestProjectPatterns { get; set; } = new();

    /// <summary>
    /// Mappings from source file patterns to corresponding test project patterns.
    /// </summary>
    [JsonPropertyName("sourceToTestMappings")]
    public List<SourceToTestMapping> SourceToTestMappings { get; set; } = [];

    /// <summary>
    /// Glob patterns for projects that produce NuGet packages or archives but may not have
    /// IsPackable=true in a csproj (e.g. eng/clipack/**).
    /// </summary>
    [JsonPropertyName("packageOrArchiveProducingProjects")]
    public List<string> PackageOrArchiveProducingProjects { get; set; } = [];

    /// <summary>
    /// Test category configurations.
    /// </summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, CategoryConfig> Categories { get; set; } = [];

    /// <summary>
    /// Loads a TestSelectorConfig from a JSON file.
    /// </summary>
    public static TestSelectorConfig LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return LoadFromJson(json);
    }

    /// <summary>
    /// Loads a TestSelectorConfig from a JSON string.
    /// </summary>
    public static TestSelectorConfig LoadFromJson(string json)
    {
        return JsonSerializer.Deserialize<TestSelectorConfig>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize config from JSON");
    }
}

/// <summary>
/// Include/exclude pattern configuration.
/// </summary>
public sealed class IncludeExcludePatterns
{
    /// <summary>
    /// Glob patterns to include.
    /// </summary>
    [JsonPropertyName("include")]
    public List<string> Include { get; set; } = [];

    /// <summary>
    /// Glob patterns to exclude from matches.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// A mapping from source file patterns to a test project pattern.
/// Supports {name} capture group substitution for flexible mapping.
/// </summary>
public sealed class SourceToTestMapping
{
    /// <summary>
    /// Glob pattern(s) for matching source files. Can include {name} capture group.
    /// In JSON, accepts either a single string or an array of strings; both shapes are
    /// normalized to this list. Example single source: <c>src/Components/{name}/**</c>;
    /// example multi-source: <c>["eng/Publishing.props", "eng/Signing.props"]</c>.
    /// </summary>
    [JsonPropertyName("source")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> Source { get; set; } = [];

    /// <summary>
    /// Pattern for the corresponding test project path. Uses {name} substitution.
    /// Example: "tests/{name}.Tests/"
    /// </summary>
    [JsonPropertyName("test")]
    public string Test { get; set; } = "";

    /// <summary>
    /// Glob patterns to exclude from this mapping. Applied to every entry in <see cref="Source"/>.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// Accepts either a JSON string or an array of strings and normalizes to a
/// <see cref="List{T}"/> of <see cref="string"/>. Used by <see cref="SourceToTestMapping.Source"/>
/// so the rules file can collapse N entries that map to the same test project into a single
/// entry with an array of source patterns.
/// </summary>
internal sealed class StringOrStringArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var single = reader.GetString();
            return string.IsNullOrEmpty(single) ? [] : [single];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Expected string elements in source array");
                }

                var value = reader.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }

            return list;
        }

        throw new JsonException("Expected 'source' to be a string or array of strings");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        // Round-trip: emit as array (the more general form). The reader still accepts both shapes
        // on the way back in.
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Configuration for a test category.
/// </summary>
public sealed class CategoryConfig
{
    /// <summary>
    /// Human-readable description of the category.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Glob patterns for files that trigger this category.
    /// </summary>
    [JsonPropertyName("triggerPaths")]
    public List<string> TriggerPaths { get; set; } = [];

    /// <summary>
    /// Glob patterns to exclude from triggerPaths.
    /// </summary>
    [JsonPropertyName("excludePaths")]
    public List<string> ExcludePaths { get; set; } = [];
}
