// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Shape checks shared by native handlers; unexpected values are never silently replaced.
/// </summary>
internal static class AgentConfigurationJson
{
    public static JsonObject ParseObject(ReadOnlySpan<byte> content)
    {
        try
        {
            // Files and inline settings use the same UTF-8 JSONC contract. A BOM is
            // accepted at the boundary; callers retain the original bytes for no-ops.
            if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                content = content[3..];
            }

            var root = JsonNode.Parse(content, documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
                ?? throw new AgentConfigurationException(AgentConfigurationStrings.ObjectRequired);
            Materialize(root);

            return root;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new AgentConfigurationException(AgentConfigurationStrings.MalformedJson);
        }
    }

    public static JsonObject? OptionalObject(JsonObject parent, string key)
        => !parent.TryGetPropertyValue(key, out var node) ? null : node as JsonObject ?? throw Shape(key);

    public static JsonObject Object(JsonObject parent, string key)
    {
        var value = OptionalObject(parent, key);
        if (value is null)
        {
            value = new JsonObject();
            parent[key] = value;
        }

        return value;
    }

    public static JsonArray? OptionalStrings(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        if (node is not JsonArray array || array.Any(value => String(value) is null))
        {
            throw Shape(key);
        }

        return array;
    }

    public static string? String(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Boolean(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : throw Shape(key);
    }

    public static AgentConfigurationException Shape(string key)
        => new(string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.UnexpectedShape, key));

    public static async Task<IReadOnlyList<JsonObject>> ReadSettingsAsync(
        AgentConfigurationMutationContext context,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var settings = new List<JsonObject>();
        foreach (var path in paths.Distinct(AgentConfigurationPath.Comparer))
        {
            if (await context.ReadOptionalAsync(path, cancellationToken) is { } root)
            {
                settings.Add(root);
            }
        }

        return settings;
    }

    private static void Materialize(JsonNode? node)
    {
        // JsonObject materializes its dictionary lazily. Inputs such as
        // {"enabledPlugins":{"aspire@aspire-skills":true,"aspire@aspire-skills":false}}
        // must be rejected here, including inside arrays, rather than throw from a
        // later mutation and prevent independent targets from being configured.
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                Materialize(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Materialize(item);
            }
        }
    }
}
