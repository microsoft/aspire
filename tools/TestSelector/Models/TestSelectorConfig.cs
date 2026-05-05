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
/// A mapping from source file patterns to test project patterns.
/// Supports {name} capture group substitution for flexible mapping.
/// </summary>
public sealed class SourceToTestMapping
{
    /// <summary>
    /// Glob pattern for matching source files. Can include {name} capture group.
    /// Example: "src/Components/{name}/**"
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>
    /// Pattern for the corresponding test project path. Uses {name} substitution.
    /// Example: "tests/{name}.Tests/"
    /// </summary>
    [JsonPropertyName("test")]
    public string Test { get; set; } = "";

    /// <summary>
    /// Glob patterns to exclude from this mapping.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
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
