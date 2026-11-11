// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Reads telemetry allowlists from the embedded <c>telemetry-allowlist.json</c> resource.
/// </summary>
internal sealed partial class AgentTelemetryCatalog
{
    internal const string AllowlistResourceName = "telemetry-allowlist.json";

    private const string SkillsPropertyName = "skills";
    private const string ReferencesPropertyName = "references";
    private const string McpToolsPropertyName = "mcpTools";

    private static readonly Lazy<AgentTelemetryCatalog> s_canonical = new(LoadCanonical);

    internal static AgentTelemetryCatalog Canonical => s_canonical.Value;

    internal IReadOnlySet<string> Skills { get; }
    internal IReadOnlySet<string> Tools { get; }
    internal IReadOnlySet<string> References { get; }

    private AgentTelemetryCatalog(HashSet<string> skills, HashSet<string> tools, HashSet<string> references)
    {
        Skills = skills;
        Tools = tools;
        References = references;
    }

    /// <summary>
    /// Parses an allowlist document.
    /// </summary>
    /// <remarks>
    /// The document has exactly three properties, each a non-empty, ordinally sorted array of unique strings:
    /// <code>
    /// {
    ///   "skills": [ "aspire", "aspire-init" ],
    ///   "references": [ "aspire-init/references/templates.md" ],
    ///   "mcpTools": [ "doctor", "list_resources" ]
    /// }
    /// </code>
    /// The file is edited by automation in the Aspire skills release workflow, so the strict shape catches
    /// malformed edits at test time instead of silently dropping telemetry.
    /// </remarks>
    internal static AgentTelemetryCatalog Parse(string json)
    {
        using var document = ParseDocument(json);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object ||
            root.EnumerateObject().Any(property => property.Name is not (SkillsPropertyName or ReferencesPropertyName or McpToolsPropertyName)))
        {
            throw new InvalidDataException("The telemetry allowlist must be an object with only skills, references, and mcpTools properties.");
        }

        var skills = ReadIdentifiers(root, SkillsPropertyName);
        var tools = ReadIdentifiers(root, McpToolsPropertyName);
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
                throw new InvalidDataException($"The telemetry allowlist contains an invalid {ReferencesPropertyName} entry.");
            }
        }

        return new(skills, tools, references);
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The telemetry allowlist is not valid JSON.", ex);
        }
    }

    private static HashSet<string> ReadIdentifiers(JsonElement root, string propertyName)
    {
        var values = ReadValues(root, propertyName);
        if (values.Any(value => !Identifier().IsMatch(value)))
        {
            throw new InvalidDataException($"The telemetry allowlist contains an invalid {propertyName} entry.");
        }

        return values;
    }

    private static HashSet<string> ReadValues(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException($"The telemetry allowlist must declare {propertyName} as an array.");
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
            {
                throw new InvalidDataException($"The telemetry allowlist contains an invalid {propertyName} entry.");
            }

            values.Add(value);
        }

        var result = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        if (values.Count == 0 || result.Count != values.Count)
        {
            throw new InvalidDataException($"The telemetry allowlist must declare a non-empty {propertyName} array without duplicates.");
        }

        // Sorted arrays keep automated edits deterministic and diffs reviewable.
        for (var i = 1; i < values.Count; i++)
        {
            if (string.CompareOrdinal(values[i - 1], values[i]) > 0)
            {
                throw new InvalidDataException($"The telemetry allowlist {propertyName} array must be sorted ordinally.");
            }
        }

        return result;
    }

    private static AgentTelemetryCatalog LoadCanonical()
    {
        // Read only the compiled allowlist. User-installed files and plugin contents must not
        // be able to introduce new telemetry dimensions.
        using var stream = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream(AllowlistResourceName)
            ?? throw new InvalidDataException("The telemetry allowlist resource is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex("""\A[a-zA-Z0-9_-]+\z""")]
    private static partial Regex Identifier();
}