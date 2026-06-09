// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestSelector.Models;

/// <summary>
/// Root configuration for the test selector.
/// </summary>
/// <remarks>
/// The config is pure relationship data: it declares which source paths map to
/// which test projects (<see cref="Mappings"/> / <see cref="Edges"/>), which paths
/// matter to everything (<see cref="RunEverything"/>) or nothing (<see cref="Ignore"/>),
/// and which standalone jobs a path fans out to (<see cref="JobCategories"/>). It
/// never encodes "if changed then run" policy — that lives entirely in
/// <see cref="TestSelector.TestEvaluator"/>.
/// </remarks>
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
    /// Glob patterns for files that should be completely ignored (no test triggers, no fallback).
    /// </summary>
    [JsonPropertyName("ignore")]
    public List<string> Ignore { get; set; } = [];

    /// <summary>
    /// Glob patterns for critical files - if any match, ALL tests run.
    /// </summary>
    [JsonPropertyName("runEverything")]
    public List<string> RunEverything { get; set; } = [];

    /// <summary>
    /// Patterns to identify test projects from affected project paths.
    /// </summary>
    [JsonPropertyName("testProjectPatterns")]
    public IncludeExcludePatterns TestProjectPatterns { get; set; } = new();

    /// <summary>
    /// Concise edge generators for conventional source→test couplings. Each entry expands
    /// (via the optional <c>{name}</c> placeholder) into one or more <c>build</c>-typed edges.
    /// These are the compact form for the component case; <c>dotnet-affected</c> usually
    /// discovers the same couplings independently.
    /// </summary>
    [JsonPropertyName("mappings")]
    public List<SelectionMapping> Mappings { get; set; } = [];

    /// <summary>
    /// Explicit edges for the non-conventional couplings, tagged by <see cref="SelectionEdge.Type"/>.
    /// Use a <c>runtime</c> edge for a coupling <c>dotnet-affected</c> cannot see because there is
    /// no <c>ProjectReference</c> (e.g. CLI end-to-end tests that consume a built archive).
    /// </summary>
    [JsonPropertyName("edges")]
    public List<SelectionEdge> Edges { get; set; } = [];

    /// <summary>
    /// Glob patterns for projects that produce NuGet packages or archives but may not have
    /// IsPackable=true in a csproj (e.g. eng/clipack/**).
    /// </summary>
    [JsonPropertyName("packageOrArchiveProducingProjects")]
    public List<string> PackageOrArchiveProducingProjects { get; set; } = [];

    /// <summary>
    /// Per-test fact (not a verb): does this test project trust the edges <c>dotnet-affected</c>
    /// infers from the MSBuild project graph? Default <see langword="true"/>. An entry set to
    /// <see langword="false"/> declares "my inferred edges are false positives" — the test then
    /// runs only when a declared <see cref="Mappings"/> or <see cref="Edges"/> entry resolves to
    /// it, not when <c>dotnet-affected</c> pulls it in transitively. This replaces the old
    /// <c>restrictedTestProjects</c> list.
    /// </summary>
    /// <remarks>
    /// Use for tests whose dependencies are genuinely self-contained — e.g. installer scripts
    /// (Acquisition.Tests) and CI/build infrastructure (Infrastructure.Tests). These cannot be
    /// broken by an Aspire.Hosting runtime change, so running them for every PR wastes CI time.
    /// Pair with a <see cref="Mappings"/> entry that maps the project's real source files so it
    /// still runs when its own surface changes.
    /// </remarks>
    [JsonPropertyName("inferDeps")]
    public Dictionary<string, bool> InferDeps { get; set; } = [];

    /// <summary>
    /// Boolean <c>run_&lt;name&gt;</c> gates for standalone jobs that do not flow through the
    /// <c>affected_test_projects</c> matrix (e.g. <c>extension</c>, <c>polyglot</c> run as dedicated
    /// jobs). The reserved name <c>integrations</c> is special: its boolean is derived from the
    /// selected test count, and its <see cref="JobCategory.When"/> glob set doubles as the
    /// "known source areas" accounting net for the conservative unmatched-files fallback and the
    /// matched-but-zero guard.
    /// </summary>
    [JsonPropertyName("jobCategories")]
    public Dictionary<string, JobCategory> JobCategories { get; set; } = [];

    /// <summary>
    /// The reserved <see cref="JobCategories"/> name whose coverage is driven by the
    /// <c>affected_test_projects</c> matrix rather than a standalone <c>run_&lt;name&gt;</c> job.
    /// </summary>
    public const string IntegrationsCategory = "integrations";

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
/// A conventional source→test edge generator. Supports <c>{name}</c> capture-group
/// substitution so one entry can cover an entire family of components.
/// </summary>
public sealed class SelectionMapping
{
    /// <summary>
    /// Glob pattern(s) for matching source files. Can include a <c>{name}</c> capture group.
    /// In JSON, accepts either a single string or an array of strings; both shapes are
    /// normalized to this list. Example single source: <c>src/Components/{name}/**</c>;
    /// example multi-source: <c>["eng/Publishing.props", "eng/Signing.props"]</c>.
    /// </summary>
    [JsonPropertyName("from")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> From { get; set; } = [];

    /// <summary>
    /// Pattern for the corresponding test project path. Uses <c>{name}</c> substitution.
    /// Example: <c>tests/{name}.Tests/{name}.Tests.csproj</c>.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>
    /// Glob patterns to exclude from this mapping. Applied to every entry in <see cref="From"/>.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// An explicit source→test edge for couplings the <see cref="SelectionMapping"/> convention
/// can't express. <see cref="Type"/> tags whether the coupling is visible to
/// <c>dotnet-affected</c> (<c>build</c>) or only at runtime (<c>runtime</c>).
/// </summary>
public sealed class SelectionEdge
{
    /// <summary>
    /// Glob pattern(s) for matching source files. Accepts a single string or an array of strings.
    /// </summary>
    [JsonPropertyName("from")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> From { get; set; } = [];

    /// <summary>
    /// Repo-root-relative <c>.csproj</c> path of the test project this edge selects.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>
    /// Edge type: <c>build</c> (default; a coupling <c>dotnet-affected</c> can also see) or
    /// <c>runtime</c> (a coupling with no <c>ProjectReference</c>, invisible to the build graph).
    /// The selection engine treats both the same for test selection; the tag documents intent and
    /// leaves room for future policy without a schema change.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "build";

    /// <summary>
    /// Optional <c>run_&lt;category&gt;</c> label. The category boolean is derived from whether
    /// <see cref="To"/> ends up in the selected test set, so the boolean and the matrix can never
    /// disagree (e.g. <c>cli_e2e</c>).
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>
    /// Glob patterns to exclude from this edge. Applied to every entry in <see cref="From"/>.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// A standalone job gate. <see cref="When"/> declares the paths that fan out to the job;
/// <see cref="Exclude"/> carves out paths that should not.
/// </summary>
public sealed class JobCategory
{
    /// <summary>
    /// Glob patterns that trigger this job category. Accepts a single string or an array.
    /// </summary>
    [JsonPropertyName("when")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> When { get; set; } = [];

    /// <summary>
    /// Glob patterns to exclude from <see cref="When"/>.
    /// </summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// Accepts either a JSON string or an array of strings and normalizes to a
/// <see cref="List{T}"/> of <see cref="string"/>. Used by glob-list properties (e.g.
/// <see cref="SelectionMapping.From"/>, <see cref="SelectionEdge.From"/>,
/// <see cref="JobCategory.When"/>) so the rules file can write either shape.
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
                    throw new JsonException("Expected string elements in glob array");
                }

                var value = reader.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }

            return list;
        }

        throw new JsonException("Expected a string or array of strings");
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
