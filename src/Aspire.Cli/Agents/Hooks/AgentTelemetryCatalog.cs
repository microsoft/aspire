// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.Mcp;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// The allowlist of Aspire-owned identifiers that agent telemetry may report. Skills and references come
/// from the embedded <c>telemetry-catalog.json</c> resource; MCP tools come from <see cref="KnownMcpTools.All"/>.
/// </summary>
/// <remarks>
/// Both the hook classifier and the <c>aspire agent telemetry</c> command check values through this type, so
/// hook scripts from older CLIs, which forward their own arguments, cannot report values outside the catalog.
/// </remarks>
internal sealed partial class AgentTelemetryCatalog
{
    internal const string CatalogResourceName = "telemetry-catalog.json";

    private const string SkillsPropertyName = "skills";
    private const string ReferencesPropertyName = "references";

    private static readonly Lazy<AgentTelemetryCatalog> s_canonical = new(LoadCanonical);

    internal static AgentTelemetryCatalog Canonical => s_canonical.Value;

    /// <summary>
    /// Gets the prefixes clients add to Aspire MCP tool names: <c>aspire-list_resources</c> (Copilot),
    /// <c>mcp__aspire__list_resources</c> (Claude Code), and <c>mcp_aspire_list_resources</c> (VS Code).
    /// </summary>
    internal static IReadOnlyList<string> McpToolPrefixes { get; } = ["aspire-", "mcp__aspire__", "mcp_aspire_"];

    private readonly FrozenSet<string> _skills;
    private readonly FrozenSet<string> _tools;
    private readonly FrozenSet<string> _references;

    private AgentTelemetryCatalog(FrozenSet<string> skills, FrozenSet<string> references)
    {
        _skills = skills;
        _tools = KnownMcpTools.All.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _references = references;
    }

    internal IReadOnlySet<string> Skills => _skills;

    /// <summary>
    /// Gets the unprefixed first-party MCP tool names, such as <c>list_resources</c>.
    /// </summary>
    internal IReadOnlySet<string> Tools => _tools;

    internal IReadOnlySet<string> References => _references;

    internal bool IsKnownSkill(string? skill)
        => skill is not null && _skills.Contains(skill);

    /// <summary>
    /// Determines whether a skill-relative path, such as <c>aspire-init/references/templates.md</c>, is a catalog reference.
    /// </summary>
    internal bool IsKnownReference(string? reference)
        => reference is not null && _references.Contains(reference);

    /// <summary>
    /// Determines whether a client-reported tool name, such as <c>mcp__aspire__list_resources</c>, is a
    /// first-party Aspire MCP tool with one of the <see cref="McpToolPrefixes"/>.
    /// </summary>
    internal bool IsKnownMcpTool(string? clientToolName)
    {
        if (clientToolName is null)
        {
            return false;
        }

        foreach (var prefix in McpToolPrefixes)
        {
            if (clientToolName.StartsWith(prefix, StringComparison.Ordinal) && _tools.Contains(clientToolName[prefix.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a catalog document.
    /// </summary>
    /// <remarks>
    /// The document has exactly two properties, each a non-empty, ordinally sorted array of unique strings:
    /// <code>
    /// {
    ///   "skills": [ "aspire", "aspire-init" ],
    ///   "references": [ "aspire-init/references/templates.md" ]
    /// }
    /// </code>
    /// The file is regenerated deterministically from Aspire skills and references, so the strict shape
    /// catches malformed edits at test time instead of silently dropping telemetry.
    /// </remarks>
    internal static AgentTelemetryCatalog Parse(string json)
    {
        using var document = ParseDocument(json);
        var root = document.RootElement;
        // JsonDocument accepts duplicate property names and TryGetProperty returns the last one, which would
        // silently discard earlier entries, so require each expected property exactly once.
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual([ReferencesPropertyName, SkillsPropertyName], StringComparer.Ordinal))
        {
            throw new InvalidDataException("The telemetry catalog must be an object with exactly one skills property and one references property.");
        }

        var skills = ReadIdentifiers(root, SkillsPropertyName);
        var references = ReadValues(root, ReferencesPropertyName);
        foreach (var reference in references)
        {
            // References are skill-relative paths such as "aspire-init/references/templates.md".
            var segments = reference.Split('/', StringSplitOptions.None);
            if (segments.Length < 3 ||
                !skills.Contains(segments[0]) ||
                !segments[1].Equals("references", StringComparison.Ordinal) ||
                reference.Contains('\\') ||
                segments.Skip(2).Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException($"The telemetry catalog contains an invalid {ReferencesPropertyName} entry.");
            }
        }

        return new(skills.ToFrozenSet(StringComparer.OrdinalIgnoreCase), references.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The telemetry catalog is not valid JSON.", ex);
        }
    }

    private static HashSet<string> ReadIdentifiers(JsonElement root, string propertyName)
    {
        var values = ReadValues(root, propertyName);
        if (values.Any(value => !Identifier().IsMatch(value)))
        {
            throw new InvalidDataException($"The telemetry catalog contains an invalid {propertyName} entry.");
        }

        return values;
    }

    private static HashSet<string> ReadValues(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException($"The telemetry catalog must declare {propertyName} as an array.");
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
            {
                throw new InvalidDataException($"The telemetry catalog contains an invalid {propertyName} entry.");
            }

            values.Add(value);
        }

        var result = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        if (values.Count == 0 || result.Count != values.Count)
        {
            throw new InvalidDataException($"The telemetry catalog must declare a non-empty {propertyName} array without duplicates.");
        }

        // Sorted arrays keep automated edits deterministic and diffs reviewable.
        for (var i = 1; i < values.Count; i++)
        {
            if (string.CompareOrdinal(values[i - 1], values[i]) > 0)
            {
                throw new InvalidDataException($"The telemetry catalog {propertyName} array must be sorted ordinally.");
            }
        }

        return result;
    }

    private static AgentTelemetryCatalog LoadCanonical()
    {
        // Read only the compiled catalog. User-installed files and plugin contents must not
        // be able to introduce new telemetry dimensions.
        using var stream = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidDataException("The telemetry catalog resource is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex("""\A[a-zA-Z0-9_-]+\z""")]
    private static partial Regex Identifier();
}