// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Agents.AspireSkills;

internal sealed class AgentPluginSource
{
    internal const string SectionName = "agentPlugins";

    // Released CLIs configured agent plugin sources under "skillSources". Existing settings
    // and configuration sources still use it, so it continues to be read. Its
    // openCodeV1Catalog/openCodeV2Catalog properties are ignored.
    // TODO: Remove the legacy alias after the migration window. https://github.com/microsoft/aspire/issues/22412
    internal const string LegacySectionName = "skillSources";
    internal const string DefaultName = "default";
    private string _name = DefaultName;

    internal static AgentPluginSource BuiltIn { get; } = new()
    {
        Repository = AspireSkillsRemoteConfiguration.Repository,
        Marketplace = AspireSkillsRemoteConfiguration.MarketplaceName,
        Plugins = ["aspire"]
    };

    [JsonPropertyName("enabled")]
    [Description("Whether to register this source. Defaults to true. Set default.enabled to false to omit the built-in Aspire source.")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("repository")]
    [Description("GitHub owner/repository or an HTTPS/SSH Git repository URL containing the agent marketplace.")]
    public string? Repository { get; init; }

    [JsonPropertyName("marketplace")]
    [Description("Agent marketplace identifier. Defaults to the source name, or aspire-skills for the default source.")]
    public string? Marketplace { get; init; }

    [JsonPropertyName("plugins")]
    [Description("Plugin names within the marketplace. Defaults to [\"aspire\"].")]
    public string[]? Plugins { get; init; }

    [JsonPropertyName("ref")]
    [Description("Optional marketplace Git ref. Existing agent-side pins are preserved.")]
    public string? Ref { get; init; }

    internal string GetName() => _name;

    internal bool IsDefault => _name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase);

    internal IEnumerable<string> GetPluginNames() => (Plugins ?? ["aspire"]).Select(plugin => $"{plugin}@{Marketplace}");

    internal string GetRepositoryUrl() => IsGitHubRepository(Repository!)
        ? $"https://github.com/{Repository}" : Repository!;

    internal static IReadOnlyList<AgentPluginSource> Resolve(DirectoryInfo configurationDirectory, FileInfo globalSettingsFile)
    {
        // Chained new/init runs before startup configuration can see newly generated or
        // materialized settings. Read the selected app's current file, not the invoking directory.
        using var effective = new ConfigurationManager();
        effective.AddEnvironmentVariables();
        ConfigurationHelper.RegisterSettingsFiles(effective, configurationDirectory, globalSettingsFile, persistNormalization: false);

        return Resolve(effective);
    }

    internal static IReadOnlyList<AgentPluginSource> Resolve(IConfigurationRoot configuration)
    {
        using var effective = new ConfigurationManager();
        var sourceSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in configuration.Providers)
        {
            // Resolve aliases inside each layer so a project override or inherited field
            // behaves the same regardless of which spelling either layer uses.
            var layer = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (provider.TryGet(SectionName, out var currentValue))
            {
                layer[SectionName] = currentValue;
            }
            if (provider.TryGet(LegacySectionName, out var legacyValue))
            {
                layer[LegacySectionName] = legacyValue;
            }

            var currentNames = provider.GetChildKeys([], SectionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in currentNames)
            {
                CopySource(name, SectionName);
            }
            foreach (var name in provider.GetChildKeys([], LegacySectionName).Except(currentNames, StringComparer.OrdinalIgnoreCase))
            {
                CopySource(name, LegacySectionName);
            }
            if (layer.Count > 0)
            {
                effective.AddInMemoryCollection(layer);
            }

            void CopySource(string name, string sectionName)
            {
                CopySection(provider, $"{sectionName}:{name}", $"{SectionName}:{name}", layer);
                sourceSections[name] = sectionName;
            }
        }

        return ResolveSources(effective, sourceSections);
    }

    private static void CopySection(IConfigurationProvider provider, string sourcePath, string targetPath, Dictionary<string, string?> layer)
    {
        if (provider.TryGet(sourcePath, out var value))
        {
            layer[targetPath] = value;
        }
        foreach (var child in provider.GetChildKeys([], sourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CopySection(provider, $"{sourcePath}:{child}", $"{targetPath}:{child}", layer);
        }
    }

    private static IReadOnlyList<AgentPluginSource> ResolveSources(IConfiguration configuration, Dictionary<string, string> sourceSections)
    {
        // Validate the registration shape for actionable errors. This does not verify
        // the safety of content supplied by the accepted marketplace or catalog.
        var section = configuration.GetSection(SectionName);
        if (section.Value is not null)
        {
            throw AspireSkillsRemoteConfiguration.ExistingConfiguration(SectionName);
        }
        var legacySection = configuration.GetSection(LegacySectionName);
        if (legacySection.Value is not null)
        {
            throw AspireSkillsRemoteConfiguration.ExistingConfiguration(LegacySectionName);
        }

        var names = new[] { DefaultName }.Concat(section.GetChildren().Select(child => child.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sources = new List<AgentPluginSource>();
        var marketplaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var settings = section.GetSection(name);
            var path = $"{sourceSections.GetValueOrDefault(name, SectionName)}.{name}";
            SourceNameValidator.Validate(name, path);
            if (settings.Value is not null)
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration(path);
            }
            if (settings.GetBool("enabled") is false)
            {
                continue;
            }

            var isDefault = name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase);
            var pluginsSection = settings.GetSection("plugins");
            var pluginEntries = pluginsSection.GetChildren().ToArray();
            var plugins = settings.GetChildren().Any(child => child.Key.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                ? pluginEntries.Select(child => child.Value ?? string.Empty).ToArray()
                : ["aspire"];
            if (pluginsSection.Value is not null || plugins.Length == 0 ||
                plugins.Any(plugin => !IsIdentifier(plugin)) ||
                pluginEntries.Where((child, index) =>
                    child.Key != index.ToString(CultureInfo.InvariantCulture) || child.GetChildren().Any()).Any())
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{path}.plugins");
            }
            var source = new AgentPluginSource
            {
                _name = name,
                Repository = settings["repository"] ?? (isDefault ? BuiltIn.Repository : null),
                Marketplace = settings["marketplace"] ?? (isDefault ? AspireSkillsRemoteConfiguration.MarketplaceName : name),
                Plugins = plugins,
                Ref = settings["ref"]
            };
            if (!IsIdentifier(source.Marketplace) || !marketplaces.Add(source.Marketplace))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{path}.marketplace");
            }
            if (string.IsNullOrWhiteSpace(source.Repository))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{path}.repository");
            }
            if (!GitHelpers.IsValidRepository(source.GetRepositoryUrl()) || !GitHelpers.IsValidRef(source.Ref))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{path}.repository/ref");
            }
            sources.Add(source);
        }

        return sources;
    }

    internal static bool IsGitHubRepository(string repository)
        => repository.Split('/') is [var owner, var repo] && IsIdentifier(owner) && IsIdentifier(repo);

    private static bool IsIdentifier(string value)
        => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
