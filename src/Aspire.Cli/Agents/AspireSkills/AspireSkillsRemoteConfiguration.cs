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

    public static bool HasPluginRegistration(IEnumerable<string> settings, IReadOnlyList<SkillSource> sources)
    {
        // Disabling new registrations does not disable a plugin already active in an agent.
        var pluginNames = sources.Select(source => source.PluginName).Prepend(PluginName).Distinct(StringComparer.Ordinal).ToArray();
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in settings)
        {
            if (AgentConfiguration.ReadObject(path) is { } config &&
                OptionalObject(config, "enabledPlugins") is { } plugins)
            {
                foreach (var pluginName in pluginNames)
                {
                    if (Boolean(plugins, pluginName) is { } value)
                    {
                        if (value)
                        {
                            enabled.Add(pluginName);
                        }
                        else
                        {
                            enabled.Remove(pluginName);
                        }
                    }
                }
            }
        }

        return enabled.Count > 0;
    }

    public static void ApplyOpenCodeCatalog(JsonObject root, IEnumerable<JsonObject> configs, IReadOnlyList<SkillSource> skillSources)
    {
        var target = ReadOpenCodeSkills(root);
        var settings = configs.ToArray();
        foreach (var source in skillSources)
        {
            var catalog = root["skills"] is JsonArray
                ? source.OpenCodeV2Catalog ?? source.OpenCodeV1Catalog
                : source.OpenCodeV1Catalog ?? source.OpenCodeV2Catalog;
            if (catalog is null)
            {
                continue;
            }
            string? preferred = null;
            foreach (var config in settings)
            {
                var sources = ReadOpenCodeSkills(config);
                if (sources?.Select(String).FirstOrDefault(value => IsOpenCodeCatalog(value, source)) is { } existing &&
                    (preferred is null || preferred.Contains("/main/opencode/", StringComparison.Ordinal)))
                {
                    preferred = existing;
                }
            }

            // V2 accepts V1 skills objects and native arrays independently of MCP.
            // Catalog URLs identify content and pins, not the surrounding JSON syntax.
            // https://github.com/anomalyco/opencode/blob/52a10f573f1da5103c85990da9341e3645a39365/services/www/src/docs/content/migrate-v1.mdx#skills
            preferred ??= catalog;
            if (target is null)
            {
                // With no skills setting, use the object form supported by both clients.
                target = new JsonArray();
                Object(root, "skills")["urls"] = target;
            }
            if (!target.Any(value => IsOpenCodeCatalog(String(value), source)))
            {
                target.Add((JsonNode?)preferred);
            }
        }
    }

    public static bool HasOpenCodeCatalog(JsonObject root, IReadOnlyList<SkillSource> sources)
        => ReadOpenCodeSkills(root)?.Any(value =>
            IsOpenCodeCatalog(String(value), SkillSource.BuiltIn) ||
            sources.Any(source => IsOpenCodeCatalog(String(value), source))) is true;

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

    private static bool IsOpenCodeCatalog(string? source, SkillSource configuration)
    {
        if (source is null)
        {
            return false;
        }
        if (source == configuration.OpenCodeV1Catalog || source == configuration.OpenCodeV2Catalog)
        {
            return true;
        }
        // Recognize existing official release/commit pins only when using the built-in
        // catalog. Custom catalog URLs are identities and must match exactly.
        if (configuration.OpenCodeV1Catalog != SkillSource.DefaultV1Catalog &&
            configuration.OpenCodeV2Catalog != SkillSource.DefaultV2Catalog)
        {
            return false;
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/microsoft/aspire-skills/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return configuration.OpenCodeV1Catalog == SkillSource.DefaultV1Catalog && path.EndsWith("/opencode/v1", StringComparison.Ordinal) ||
            configuration.OpenCodeV2Catalog == SkillSource.DefaultV2Catalog && path.EndsWith("/opencode/v2", StringComparison.Ordinal);
    }

    public static async Task ApplyPluginAsync(
        JsonObject root, IEnumerable<string> settingsPaths, string targetPath,
        AgentConfiguration edit, IReadOnlyList<SkillSource> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return;
        }
        var settings = await ReadSettingsAsync(settingsPaths, targetPath, edit, cancellationToken);
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
        foreach (var skillSource in sources)
        {
            ApplyPlugin(root, settings, skillSource);
        }
    }

    private static void ApplyPlugin(JsonObject root, IReadOnlyList<JsonObject> settings, SkillSource skillSource)
    {
        var marketplaceName = skillSource.Marketplace!;
        var pluginName = skillSource.PluginName;
        JsonObject? selectedSource = null;
        foreach (var config in settings.Append(root))
        {
            var plugins = OptionalObject(config, "enabledPlugins");
            if (plugins is not null && Boolean(plugins, pluginName) is false)
            {
                throw ExistingConfiguration($"enabledPlugins.{pluginName}");
            }

            var marketplaces = OptionalObject(config, "extraKnownMarketplaces");
            if (marketplaces is not null && marketplaces.TryGetPropertyValue(marketplaceName, out var marketplaceNode))
            {
                ArgumentNullException.ThrowIfNull(marketplaceNode);
                var source = OptionalObject(marketplaceNode.AsObject(), "source");
                ArgumentNullException.ThrowIfNull(source);

                if (!IsMatchingSource(source, skillSource))
                {
                    throw ExistingConfiguration($"extraKnownMarketplaces.{marketplaceName}.source");
                }

                // Keep inherited release/commit pins unless the destination declares its own source.
                if (ReferenceEquals(config, root) || source.ContainsKey("ref") || source.ContainsKey("sha") || selectedSource is null)
                {
                    selectedSource = source;
                }
            }
        }

        if (selectedSource is null)
        {
            selectedSource = SkillSource.IsGitHubRepository(skillSource.Repository!)
                ? new JsonObject { ["source"] = "github", ["repo"] = skillSource.Repository }
                : new JsonObject { ["source"] = "git", ["url"] = skillSource.Repository };
            if (skillSource.Ref is not null)
            {
                selectedSource["ref"] = skillSource.Ref;
            }
        }

        var targetMarketplaces = Object(root, "extraKnownMarketplaces");
        if (!targetMarketplaces.ContainsKey(marketplaceName))
        {
            targetMarketplaces[marketplaceName] = new JsonObject
            {
                ["source"] = selectedSource.DeepClone()
            };
        }

        // No autoUpdate opt-in and no installedPlugins/cache metadata: registration is not
        // acquisition. Existing entries, including explicit false, were checked above.
        var targetPlugins = Object(root, "enabledPlugins");
        if (!targetPlugins.ContainsKey(pluginName))
        {
            targetPlugins[pluginName] = true;
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

    private static bool IsMatchingSource(JsonObject source, SkillSource configuration)
    {
        return String(source["source"]) switch
        {
            "github" => String(source["repo"]) is { } repository &&
                string.Equals(NormalizeRepositoryUrl($"https://github.com/{repository}"),
                    NormalizeRepositoryUrl(configuration.RepositoryUrl), StringComparison.OrdinalIgnoreCase),
            "git" => String(source["url"]) is { } url &&
                string.Equals(NormalizeRepositoryUrl(url), NormalizeRepositoryUrl(configuration.RepositoryUrl),
                    Uri.TryCreate(configuration.RepositoryUrl, UriKind.Absolute, out var repositoryUri) &&
                        repositoryUri.Scheme == Uri.UriSchemeHttps && repositoryUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            _ => false
        };
    }

    private static string NormalizeRepositoryUrl(string url)
    {
        url = url.TrimEnd('/');
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }
}
