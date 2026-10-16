// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Shared Copilot/Claude plugin registration without acquiring or activating it.
/// </summary>
internal static class AspireSkillsConfiguration
{
    internal const string MarketplaceName = "aspire-skills";
    internal const string PluginName = "aspire@aspire-skills";
    internal const string Repository = "microsoft/aspire-skills";
    internal const string RepositoryUrl = "https://github.com/" + Repository;

    public static AgentFileEdit CreatePluginEdit(IAgentClient client, string path, IEnumerable<string> settings)
        => new(client, path, static bytes => AgentConfigurationJson.ParseObject(bytes.Span),
            async (root, reader, token) => ApplyPlugin(root, await AgentConfigurationJson.ReadSettingsAsync(reader, settings, token)));

    private static AgentConfigurationEdit ApplyPlugin(JsonObject root, IReadOnlyList<JsonObject> settings)
    {
        JsonObject? selectedSource = null;
        foreach (var config in settings.Append(root))
        {
            var plugins = AgentConfigurationJson.OptionalObject(config, "enabledPlugins");
            if (plugins is not null && AgentConfigurationJson.Boolean(plugins, PluginName) is false)
            {
                return AgentConfigurationEdit.Skipped(AgentCommandStrings.Configuration_Disabled);
            }

            var marketplaces = AgentConfigurationJson.OptionalObject(config, "extraKnownMarketplaces");
            if (marketplaces is not null && marketplaces.TryGetPropertyValue(MarketplaceName, out var marketplaceNode))
            {
                if (marketplaceNode is not JsonObject marketplace ||
                    AgentConfigurationJson.OptionalObject(marketplace, "source") is not { } source)
                {
                    throw AgentConfigurationJson.Shape($"extraKnownMarketplaces.{MarketplaceName}.source");
                }

                if (!IsOfficialSource(source))
                {
                    return AgentConfigurationEdit.Blocked(AgentCommandStrings.Configuration_Conflict);
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
        if (settings.Append(root).Any(config =>
            config.ContainsKey("strictKnownMarketplaces") || config.ContainsKey("blockedMarketplaces")))
        {
            return AgentConfigurationEdit.Blocked(AgentCommandStrings.Configuration_PolicyBlocked);
        }

        var targetMarketplaces = AgentConfigurationJson.Object(root, "extraKnownMarketplaces");
        if (!targetMarketplaces.ContainsKey(MarketplaceName))
        {
            targetMarketplaces[MarketplaceName] = new JsonObject
            {
                ["source"] = selectedSource.DeepClone()
            };
        }

        // No autoUpdate opt-in and no installedPlugins/cache metadata: registration is not
        // acquisition. Existing entries, including explicit false, were checked above.
        var targetPlugins = AgentConfigurationJson.Object(root, "enabledPlugins");
        if (!targetPlugins.ContainsKey(PluginName))
        {
            targetPlugins[PluginName] = true;
        }

        return AgentConfigurationEdit.Applied();
    }

    private static bool IsOfficialSource(JsonObject source)
    {
        return AgentConfigurationJson.String(source["source"]) switch
        {
            "github" => string.Equals(AgentConfigurationJson.String(source["repo"]), Repository, StringComparison.OrdinalIgnoreCase),
            "git" => AgentConfigurationJson.String(source["url"]) is { } url &&
                (string.Equals(url.TrimEnd('/'), $"https://github.com/{Repository}", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(url.TrimEnd('/'), $"https://github.com/{Repository}.git", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}
