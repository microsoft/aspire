// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Registers the official marketplace without invoking plugin CLIs or changing installation state.
/// </summary>
internal static class PluginConfiguration
{
    internal const string MarketplaceName = "aspire-skills";
    internal const string PluginName = "aspire@aspire-skills";
    internal const string Repository = "microsoft/aspire-skills";

    public static AgentConfigurationEdit Apply(JsonObject root, IReadOnlyList<JsonObject> settings)
    {
        JsonObject? pinnedSource = null;
        foreach (var config in settings.Append(root))
        {
            var plugins = AgentConfigurationJson.OptionalObject(config, "enabledPlugins");
            if (plugins is not null && AgentConfigurationJson.Boolean(plugins, PluginName) is false)
            {
                return AgentConfigurationEdit.Skipped(AgentConfigurationStrings.Disabled);
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
                    return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.Conflict);
                }

                // In particular, do not shadow a user's release/commit pin by adding an
                // unpinned project marketplace. Keep the source object exactly as declared.
                if (source.ContainsKey("ref") || source.ContainsKey("sha") || pinnedSource is null)
                {
                    pinnedSource = source;
                }
            }

            if (HasMarketplacePolicyConflict(config))
            {
                return AgentConfigurationEdit.Blocked(AgentConfigurationStrings.PolicyBlocked);
            }
        }

        var targetMarketplaces = AgentConfigurationJson.Object(root, "extraKnownMarketplaces");
        if (!targetMarketplaces.ContainsKey(MarketplaceName))
        {
            targetMarketplaces[MarketplaceName] = new JsonObject
            {
                ["source"] = pinnedSource?.DeepClone() ?? new JsonObject
                {
                    ["source"] = "github",
                    ["repo"] = Repository
                }
            };
        }

        // No autoUpdate opt-in and no installedPlugins/cache metadata: registration is not
        // acquisition. Existing entries, including explicit false, were checked above.
        var targetPlugins = AgentConfigurationJson.Object(root, "enabledPlugins");
        if (!targetPlugins.ContainsKey(PluginName))
        {
            targetPlugins[PluginName] = true;
        }

        return AgentConfigurationEdit.Applied(AgentConfigurationStrings.Registered);
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

    private static bool HasMarketplacePolicyConflict(JsonObject config)
    {
        // Claude's managed marketplace lists match source objects, not marketplace display
        // names. Unknown matcher forms cannot be established offline and are left to the client.
        // https://code.claude.com/docs/en/settings-reference#strictknownmarketplaces
        if (config.TryGetPropertyValue("strictKnownMarketplaces", out var allowedNode) &&
            (allowedNode is not JsonArray allowed || !allowed.OfType<JsonObject>().Any(IsOfficialSource)))
        {
            return true;
        }

        if (config.TryGetPropertyValue("blockedMarketplaces", out var blockedNode) &&
            (blockedNode is not JsonArray blocked || blocked.Any(entry => entry is not JsonObject source || IsOfficialSource(source) ||
                AgentConfigurationJson.String(source["source"]) is not ("github" or "git"))))
        {
            return true;
        }

        return false;
    }
}
