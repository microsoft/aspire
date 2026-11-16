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
    internal const string OpenCodeV1Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/";
    internal const string OpenCodeV2Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v2/";

    public static bool HasPluginRegistration(IEnumerable<string> settings, IReadOnlyList<AgentPluginSource> sources)
    {
        // Disabling new registrations does not disable a plugin already active in an agent.
        var pluginNames = sources.SelectMany(source => source.GetPluginNames()).Prepend(PluginName).Distinct(StringComparer.Ordinal).ToArray();
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in settings)
        {
            // A JSON null registers nothing, so it reads like a missing key. Other wrong types still throw
            // InvalidOperationException from AsObject or GetValue.
            if (AgentConfiguration.ReadObject(path)?["enabledPlugins"] is { } pluginsNode)
            {
                var plugins = pluginsNode.AsObject();
                foreach (var pluginName in pluginNames)
                {
                    if (plugins[pluginName] is { } valueNode)
                    {
                        if (valueNode.GetValue<bool>())
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

    // OpenCode consumes skill catalogs rather than plugin marketplaces, so agent plugin sources
    // cannot be registered with it. It only receives the built-in Aspire catalog.
    public static void ApplyOpenCodeCatalog(JsonObject root, IEnumerable<JsonObject> configs)
    {
        var catalog = root["skills"] is JsonArray ? OpenCodeV2Catalog : OpenCodeV1Catalog;
        string? preferred = null;
        foreach (var config in configs)
        {
            if (ReadOpenCodeSkills(config)?.Select(String).FirstOrDefault(IsOpenCodeCatalog) is { } existing &&
                (preferred is null || preferred.Contains("/main/opencode/", StringComparison.Ordinal)))
            {
                preferred = existing;
            }
        }

        // V2 accepts V1 skills objects and native arrays independently of MCP.
        // Catalog URLs identify content and pins, not the surrounding JSON syntax.
        // https://github.com/anomalyco/opencode/blob/52a10f573f1da5103c85990da9341e3645a39365/services/www/src/docs/content/migrate-v1.mdx#skills
        preferred ??= catalog;
        var target = ReadOpenCodeSkills(root);
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

    internal static JsonArray? ReadOpenCodeSkills(JsonObject root)
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

    internal static bool IsOpenCodeCatalog(string? source)
    {
        if (source is null)
        {
            return false;
        }
        if (source is OpenCodeV1Catalog or OpenCodeV2Catalog)
        {
            return true;
        }
        // Also recognize existing official release/commit pins, for example
        // https://raw.githubusercontent.com/microsoft/aspire-skills/v1.2.0/opencode/v2/
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/microsoft/aspire-skills/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/opencode/v1", StringComparison.Ordinal) || path.EndsWith("/opencode/v2", StringComparison.Ordinal);
    }

    public static async Task ApplyPluginAsync(
        JsonObject root, IEnumerable<string> settingsPaths, string targetPath,
        AgentConfiguration edit, IReadOnlyList<AgentPluginSource> sources, CancellationToken cancellationToken)
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
        foreach (var agentPluginSource in sources)
        {
            ApplyPlugin(root, settings, agentPluginSource);
        }
    }

    private static void ApplyPlugin(JsonObject root, IReadOnlyList<JsonObject> settings, AgentPluginSource agentPluginSource)
    {
        var marketplaceName = agentPluginSource.Marketplace!;
        var pluginNames = agentPluginSource.GetPluginNames().ToArray();
        JsonObject? selectedSource = null;
        foreach (var config in settings.Append(root))
        {
            var plugins = OptionalObject(config, "enabledPlugins");
            foreach (var pluginName in pluginNames)
            {
                if (plugins is not null && Boolean(plugins, pluginName) is false)
                {
                    throw ExistingConfiguration($"enabledPlugins.{pluginName}");
                }
            }

            var marketplaces = OptionalObject(config, "extraKnownMarketplaces");
            if (marketplaces is not null && marketplaces.TryGetPropertyValue(marketplaceName, out var marketplaceNode))
            {
                ArgumentNullException.ThrowIfNull(marketplaceNode);
                var source = OptionalObject(marketplaceNode.AsObject(), "source");
                ArgumentNullException.ThrowIfNull(source);

                if (!IsMatchingSource(source, agentPluginSource))
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
            selectedSource = AgentPluginSource.IsGitHubRepository(agentPluginSource.Repository!)
                ? new JsonObject { ["source"] = "github", ["repo"] = agentPluginSource.Repository }
                : new JsonObject { ["source"] = "git", ["url"] = agentPluginSource.Repository };
            if (agentPluginSource.Ref is not null)
            {
                selectedSource["ref"] = agentPluginSource.Ref;
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
        foreach (var pluginName in pluginNames)
        {
            if (!targetPlugins.ContainsKey(pluginName))
            {
                targetPlugins[pluginName] = true;
            }
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

    private static bool IsMatchingSource(JsonObject source, AgentPluginSource configuration)
    {
        return String(source["source"]) switch
        {
            "github" => String(source["repo"]) is { } repository &&
                string.Equals(NormalizeRepositoryUrl($"https://github.com/{repository}"),
                    NormalizeRepositoryUrl(configuration.GetRepositoryUrl()), StringComparison.OrdinalIgnoreCase),
            "git" => String(source["url"]) is { } url &&
                string.Equals(NormalizeRepositoryUrl(url), NormalizeRepositoryUrl(configuration.GetRepositoryUrl()),
                    Uri.TryCreate(configuration.GetRepositoryUrl(), UriKind.Absolute, out var repositoryUri) &&
                        repositoryUri.Scheme == Uri.UriSchemeHttps && repositoryUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            _ => false
        };
    }

    internal static string NormalizeRepositoryUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
        {
            // AbsoluteUri normalizes HTTPS://GIT.EXAMPLE.COM/team/skills to lowercase scheme/host,
            // preserving case-sensitive repository paths and SSH usernames.
            // https://www.rfc-editor.org/rfc/rfc3986#section-6.2.2.1
            url = uri.AbsoluteUri;
        }

        url = url.TrimEnd('/');
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }
}
