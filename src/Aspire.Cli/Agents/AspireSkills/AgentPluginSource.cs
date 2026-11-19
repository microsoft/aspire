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

    internal static IReadOnlyList<AgentPluginSource> Resolve(IConfiguration configuration)
    {
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

        // A source name defined in agentPlugins replaces the whole legacy skillSources entry
        // with the same name, rather than merging properties, so the two formats never mix
        // within one source. Use child keys rather than Exists(), which is false for {}.
        var currentNames = section.GetChildren().Select(child => child.Key).ToArray();
        var currentNameSet = currentNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyNames = legacySection.GetChildren().Select(child => child.Key).ToArray();
        var names = new[] { DefaultName }.Concat(currentNames).Concat(legacyNames)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sources = new List<AgentPluginSource>();
        var marketplaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var sectionName = currentNameSet.Contains(name) || !legacyNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? SectionName
                : LegacySectionName;
            var settings = configuration.GetSection(sectionName).GetSection(name);
            var path = $"{sectionName}.{name}";
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
