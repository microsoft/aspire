// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Aspire plugin and OpenCode catalog registration without acquiring their content.
/// </summary>
internal static class AspireSkillsRemoteConfiguration
{
    internal const string MarketplaceName = "aspire-skills";
    internal const string PluginName = "aspire@aspire-skills";
    internal const string Repository = "microsoft/aspire-skills";
    internal const string RepositoryUrl = "https://github.com/" + Repository;

    private const string OpenCodeV1Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/";
    private const string OpenCodeV2Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v2/";

    public static bool HasPluginRegistration(IEnumerable<string> settings, CancellationToken cancellationToken)
    {
        var enabled = false;
        foreach (var path in settings)
        {
            if (AgentConfiguration.ReadObject(path, cancellationToken) is { } config &&
                OptionalObject(config, "enabledPlugins") is { } plugins &&
                Boolean(plugins, PluginName) is { } value)
            {
                enabled = value;
            }
        }

        return enabled;
    }

    public static void ApplyOpenCodeCatalog(JsonObject root, IEnumerable<JsonObject> configs)
    {
        var target = ReadOpenCodeSkills(root);
        string? preferred = null;
        foreach (var config in configs)
        {
            var sources = ReadOpenCodeSkills(config);
            if (sources?.Select(String).FirstOrDefault(IsOpenCodeCatalog) is { } existing &&
                (preferred is null || preferred.Contains("/main/opencode/", StringComparison.Ordinal)))
            {
                preferred = existing;
            }
        }

        // V2 accepts V1 skills objects and native arrays independently of MCP.
        // Catalog URLs identify content and pins, not the surrounding JSON syntax.
        // https://github.com/anomalyco/opencode/blob/52a10f573f1da5103c85990da9341e3645a39365/services/www/src/docs/content/migrate-v1.mdx#skills
        preferred ??= root["skills"] is JsonArray ? OpenCodeV2Catalog : OpenCodeV1Catalog;
        if (target is null)
        {
            // With no skills setting, use the object form supported by both clients.
            target = new JsonArray();
            Object(root, "skills")["urls"] = target;
        }
        if (!target.Any(value => IsOpenCodeCatalog(String(value))))
        {
            target.Add((JsonNode?)preferred);
        }
    }

    public static bool HasOpenCodeCatalog(JsonObject root)
        => ReadOpenCodeSkills(root)?.Any(value => IsOpenCodeCatalog(String(value))) is true;

    private static JsonArray? ReadOpenCodeSkills(JsonObject root)
    {
        if (!root.TryGetPropertyValue("skills", out var skills))
        {
            return null;
        }

        if (skills is JsonObject v1)
        {
            OptionalStrings(v1, "paths");
            return OptionalStrings(v1, "urls");
        }
        ArgumentNullException.ThrowIfNull(skills);
        return Strings(skills.AsArray());
    }

    private static bool IsOpenCodeCatalog(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/microsoft/aspire-skills/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/opencode/v1", StringComparison.Ordinal) ||
            path.EndsWith("/opencode/v2", StringComparison.Ordinal);
    }

    public static async Task ApplyPluginAsync(
        JsonObject root, IEnumerable<string> settingsPaths, string targetPath,
        AgentConfiguration edit, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(settingsPaths, targetPath, edit, cancellationToken);
        JsonObject? selectedSource = null;
        foreach (var config in settings.Append(root))
        {
            var plugins = OptionalObject(config, "enabledPlugins");
            if (plugins is not null && Boolean(plugins, PluginName) is false)
            {
                throw ExistingConfiguration($"enabledPlugins.{PluginName}");
            }

            var marketplaces = OptionalObject(config, "extraKnownMarketplaces");
            if (marketplaces is not null && marketplaces.TryGetPropertyValue(MarketplaceName, out var marketplaceNode))
            {
                ArgumentNullException.ThrowIfNull(marketplaceNode);
                var source = OptionalObject(marketplaceNode.AsObject(), "source");
                ArgumentNullException.ThrowIfNull(source);

                if (!IsOfficialSource(source))
                {
                    throw ExistingConfiguration($"extraKnownMarketplaces.{MarketplaceName}.source");
                }

                // Keep inherited release/commit pins unless the destination declares its own source.
                if (ReferenceEquals(config, root) || source.ContainsKey("ref") || source.ContainsKey("sha") || selectedSource is null)
                {
                    selectedSource = source;
                }
            }
        }

        selectedSource ??= new JsonObject
        {
            ["source"] = "github",
            ["repo"] = Repository
        };
        // Leave marketplace policy enforcement to the client rather than matching its rules.
        foreach (var config in settings.Append(root))
        {
            foreach (var policy in new[] { "strictKnownMarketplaces", "blockedMarketplaces" })
            {
                if (config.ContainsKey(policy))
                {
                    throw ExistingConfiguration(policy);
                }
            }
        }

        var targetMarketplaces = Object(root, "extraKnownMarketplaces");
        if (!targetMarketplaces.ContainsKey(MarketplaceName))
        {
            targetMarketplaces[MarketplaceName] = new JsonObject
            {
                ["source"] = selectedSource.DeepClone()
            };
        }

        // No autoUpdate opt-in and no installedPlugins/cache metadata: registration is not
        // acquisition. Existing entries, including explicit false, were checked above.
        var targetPlugins = Object(root, "enabledPlugins");
        if (!targetPlugins.ContainsKey(PluginName))
        {
            targetPlugins[PluginName] = true;
        }
    }

    public static async Task<IReadOnlyList<JsonObject>> ReadSettingsAsync(
        IEnumerable<string> paths, string targetPath, AgentConfiguration edit, CancellationToken cancellationToken)
    {
        var settings = new List<JsonObject>();
        foreach (var path in paths.Distinct(StringComparers.FileSystemPath))
        {
            if (!string.Equals(path, targetPath, StringComparisons.FileSystemPath) &&
                await edit.ReadObjectForEditAsync(path, cancellationToken) is { } root)
            {
                settings.Add(root);
            }
        }

        return settings;
    }

    public static JsonObject? OptionalObject(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(node, key);
        return node.AsObject();
    }

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

        ArgumentNullException.ThrowIfNull(node, key);
        return Strings(node.AsArray());
    }

    public static JsonArray Strings(JsonArray array)
    {
        foreach (var value in array)
        {
            ArgumentNullException.ThrowIfNull(value);
            _ = value.GetValue<string>();
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

        ArgumentNullException.ThrowIfNull(node, key);
        return node.GetValue<bool>();
    }

    internal static InvalidOperationException ExistingConfiguration(string key)
        => new(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ExistingConfigurationConflict, key));

    private static bool IsOfficialSource(JsonObject source)
    {
        return String(source["source"]) switch
        {
            "github" => string.Equals(String(source["repo"]), Repository, StringComparison.OrdinalIgnoreCase),
            "git" => String(source["url"]) is { } url &&
                (string.Equals(url.TrimEnd('/'), $"https://github.com/{Repository}", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(url.TrimEnd('/'), $"https://github.com/{Repository}.git", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}
