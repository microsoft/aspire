// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Packaging;

internal sealed class IntegrationSourceConfiguration
{
    internal const string SectionName = "integrationSources";
    internal const string DefaultName = "default";

    [JsonPropertyName("enabled")]
    [Description("Whether to search this source. Defaults to true. Disable default to omit built-in channel and NuGet discovery.")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("source")]
    [Description("NuGet service index URL or local package directory. The default entry uses built-in discovery when omitted.")]
    public string? Source { get; init; }

    [JsonPropertyName("patterns")]
    [Description("Optional allowed package IDs: exact IDs, prefixes ending in *, or * for all packages. Defaults to * when omitted.")]
    public string[]? Patterns { get; init; }

    [JsonIgnore]
    internal string Name { get; init; } = DefaultName;

    internal bool AllowsPackage(string packageId)
        => Patterns!.Any(pattern => PackageSourceOverrideMappings.MatchesPackage(pattern, packageId));

    internal string SearchTerm
    {
        get
        {
            var prefix = Patterns![0].TrimEnd('*');
            foreach (var pattern in Patterns.Skip(1))
            {
                var length = Math.Min(prefix.Length, pattern.TrimEnd('*').Length);
                while (!pattern.AsSpan().StartsWith(prefix.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                {
                    length--;
                }
                prefix = prefix[..length];
            }

            return prefix;
        }
    }

    internal static IReadOnlyList<IntegrationSourceConfiguration> Resolve(DirectoryInfo directory, FileInfo globalSettingsFile)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddEnvironmentVariables();
        ConfigurationHelper.RegisterSettingsFiles(configuration, directory, globalSettingsFile, persistNormalization: false);

        return Resolve(configuration, directory);
    }

    internal static IReadOnlyList<IntegrationSourceConfiguration> Resolve(IConfiguration configuration, DirectoryInfo directory)
    {
        var section = configuration.GetSection(SectionName);
        if (section.Value is not null)
        {
            throw InvalidConfiguration(SectionName);
        }

        var sources = new List<IntegrationSourceConfiguration>();
        foreach (var name in new[] { DefaultName }.Concat(section.GetChildren().Select(child => child.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var settings = section.GetSection(name);
            var key = $"{SectionName}.{name}";
            if (!IsIdentifier(name) || settings.Value is not null)
            {
                throw InvalidConfiguration(key);
            }
            if (settings.GetBool("enabled") is false)
            {
                continue;
            }

            var source = settings["source"];
            var patternsSection = settings.GetSection("patterns");
            var patterns = patternsSection.GetChildren().Select(child => child.Value).ToArray();
            if (source is null && name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) &&
                patternsSection.Value is null && patterns.Length == 0)
            {
                sources.Add(new IntegrationSourceConfiguration());
                continue;
            }
            if (string.IsNullOrWhiteSpace(source) || PackageSourceOverrideMappings.HasCredentialMaterial(source) ||
                Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme is not ("https" or "http"))
            {
                throw InvalidConfiguration($"{key}.source");
            }
            if (!settings.GetChildren().Any(child => child.Key.Equals("patterns", StringComparison.OrdinalIgnoreCase)))
            {
                patterns = ["*"];
            }
            if (patternsSection.Value is not null || patterns.Length == 0 ||
                patternsSection.GetChildren().Where((child, index) => child.Key != index.ToString(CultureInfo.InvariantCulture)).Any() ||
                patterns.Any(pattern => pattern is null || !IsPattern(pattern)))
            {
                throw InvalidConfiguration($"{key}.patterns");
            }

            sources.Add(new IntegrationSourceConfiguration
            {
                Name = name,
                Source = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, directory),
                Patterns = patterns.Select(pattern => pattern!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            });
        }

        return
        [
            .. sources.Where(source => source.Source is null),
            .. sources.Where(source => source.Source is not null)
                .GroupBy(source => source.Source!, PackageSourceIdentity.Comparer)
                .Select(group => new IntegrationSourceConfiguration
                {
                    Name = group.First().Name,
                    Source = group.First().Source,
                    Patterns = group.SelectMany(source => source.Patterns!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                })
        ];
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsPattern(string pattern)
        => pattern == "*" || IsIdentifier(pattern.EndsWith('*') ? pattern[..^1] : pattern);

    private static InvalidOperationException InvalidConfiguration(string key)
        => new(string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidIntegrationSource, key));
}
