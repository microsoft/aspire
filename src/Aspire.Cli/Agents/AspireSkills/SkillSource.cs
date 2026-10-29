// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Aspire.Cli.Git;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Agents.AspireSkills;

internal sealed class SkillSource
{
    internal const string SectionName = "skillSources";
    internal const string DefaultName = "default";
    internal const string DefaultV1Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/";
    internal const string DefaultV2Catalog = "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v2/";

    internal static SkillSource BuiltIn { get; } = new()
    {
        Repository = AspireSkillsRemoteConfiguration.Repository,
        Marketplace = AspireSkillsRemoteConfiguration.MarketplaceName,
        Plugins = ["aspire"],
        OpenCodeV1Catalog = DefaultV1Catalog,
        OpenCodeV2Catalog = DefaultV2Catalog
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
    public string[] Plugins { get; init; } = ["aspire"];

    [JsonPropertyName("ref")]
    [Description("Optional marketplace Git ref. Existing agent-side pins are preserved.")]
    public string? Ref { get; init; }

    [JsonPropertyName("openCodeV1Catalog")]
    [Description("HTTPS skills catalog URL for OpenCode V1. Omit for sources without an OpenCode catalog.")]
    public string? OpenCodeV1Catalog { get; init; }

    [JsonPropertyName("openCodeV2Catalog")]
    [Description("HTTPS skills catalog URL for OpenCode V2. Omit for sources without an OpenCode catalog.")]
    public string? OpenCodeV2Catalog { get; init; }

    [JsonIgnore]
    internal IEnumerable<string> PluginNames => Plugins.Select(plugin => $"{plugin}@{Marketplace}");

    [JsonIgnore]
    internal string RepositoryUrl => IsGitHubRepository(Repository!)
        ? $"https://github.com/{Repository}" : Repository!;

    internal static IReadOnlyList<SkillSource> Resolve(DirectoryInfo configurationDirectory, FileInfo globalSettingsFile)
    {
        // Chained new/init runs before startup configuration can see newly generated or
        // materialized settings. Read the selected app's current file, not the invoking directory.
        using var effective = new ConfigurationManager();
        effective.AddEnvironmentVariables();
        ConfigurationHelper.RegisterSettingsFiles(effective, configurationDirectory, globalSettingsFile, persistNormalization: false);

        return Resolve(effective);
    }

    internal static IReadOnlyList<SkillSource> Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (section.Value is not null)
        {
            throw AspireSkillsRemoteConfiguration.ExistingConfiguration(SectionName);
        }

        var names = new[] { DefaultName }.Concat(section.GetChildren().Select(child => child.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sources = new List<SkillSource>();
        var marketplaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var settings = section.GetSection(name);
            if (settings.Value is not null || !IsIdentifier(name))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}");
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
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}.plugins");
            }
            var source = new SkillSource
            {
                Repository = settings["repository"] ?? (isDefault ? BuiltIn.Repository : null),
                Marketplace = settings["marketplace"] ?? (isDefault ? AspireSkillsRemoteConfiguration.MarketplaceName : name),
                Plugins = plugins,
                Ref = settings["ref"],
                OpenCodeV1Catalog = settings["openCodeV1Catalog"] ?? (isDefault ? BuiltIn.OpenCodeV1Catalog : null),
                OpenCodeV2Catalog = settings["openCodeV2Catalog"] ?? (isDefault ? BuiltIn.OpenCodeV2Catalog : null)
            };
            if (!IsIdentifier(source.Marketplace) || !marketplaces.Add(source.Marketplace))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}.marketplace");
            }
            if (string.IsNullOrWhiteSpace(source.Repository))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}.repository");
            }
            if (!GitSourceValidator.IsValidRepository(source.RepositoryUrl) || !GitSourceValidator.IsValidRef(source.Ref))
            {
                throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}.repository/ref");
            }
            foreach (var catalog in new[] { source.OpenCodeV1Catalog, source.OpenCodeV2Catalog })
            {
                if (catalog is not null &&
                    (!Uri.TryCreate(catalog, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                     uri.UserInfo.Length != 0 || uri.Fragment.Length != 0))
                {
                    throw AspireSkillsRemoteConfiguration.ExistingConfiguration($"{SectionName}.{name}.openCodeCatalog");
                }
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
