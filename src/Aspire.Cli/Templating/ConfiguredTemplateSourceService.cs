// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

internal sealed class ConfiguredTemplateSourceService(
    IConfigurationService configurationService,
    ITemplateProvider templateProvider,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    ConfigSourceService configSources,
    ILogger<ConfiguredTemplateSourceService> logger)
{
    internal async Task<IReadOnlyList<KeyValuePair<string, ConfiguredTemplateSource>>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var settingsPath = configurationService.GetSettingsFilePath(isGlobal: true);
        AspireConfigFile? settings;
        try
        {
            settings = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(settingsPath, cancellationToken), JsonSourceGenerationContext.Default.AspireConfigFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogDebug("No global template source settings file was found.");
            settings = null;
        }

        var inherited = await configSources.GetTemplateSourcesAsync(cancellationToken);
        var inheritedSettings = new JsonObject
        {
            [AspireConfigFile.TemplateSourcesSectionName] = inherited
        }.Deserialize(JsonSourceGenerationContext.Default.AspireConfigFile);
        var definitions = TemplateSourceConfiguration.MergeSources(
            inheritedSettings?.TemplateSources ?? [], settings?.TemplateSources ?? []);
        logger.LogDebug("Merged {InheritedCount} inherited and {GlobalCount} global template source definitions into {SourceCount} sources; global definitions take precedence.",
            inherited.Count, settings?.TemplateSources?.Count ?? 0, definitions.Count);
        List<KeyValuePair<string, ConfiguredTemplateSource>> sources = [];
        foreach (var (name, definition) in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateSourceConfiguration.ValidateName(name);
            if (definition is null)
            {
                throw new TemplateSourceConfigurationException($"{AspireConfigFile.TemplateSourcesSectionName}.{name}", NewCommandStrings.InvalidConfiguredTemplateSource);
            }
            var source = TemplateSourceConfiguration.Parse(
                definition.Path, definition.Repository, definition.Ref, definition.DisplayName, $"{AspireConfigFile.TemplateSourcesSectionName}.{name}", requireComplete: true);
            var isGlobal = settings?.TemplateSources?.Keys.Any(key => key.Equals(name, StringComparisons.TemplateSourceName)) is true;
            logger.LogDebug("Using template source {SourceName} from {SourceOrigin} settings (repository: {IsRepository}).",
                name, isGlobal ? "global" : "configuration source", source.Template?.Repository is not null);
            sources.Add(new(name, source));
        }

        return sources;
    }

    public async Task<IReadOnlyList<ConfiguredTemplate>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            logger.LogDebug("Skipping configured template search because no sources are configured.");
            interactionService.DisplayMessage(KnownEmojis.Information, NewCommandStrings.NoConfiguredTemplateSources);
            return [];
        }

        interactionService.DisplayTemplateSources(sources.Select(source =>
            (source.Value.DisplayName ?? source.Key,
                source.Value.Template ?? throw new InvalidOperationException("A complete template source must have a location."))),
            executionContext);

        List<ConfiguredTemplate> results = [];
        // The template engine owns mutable mount/resource state. Discover sequentially rather
        // than sharing a factory across concurrent source installations.
        foreach (var (name, configuredSource) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var source = configuredSource.Template
                    ?? throw new InvalidOperationException("A complete template source must have a location.");
                var templates = await templateProvider.GetTemplatesAsync(source, cancellationToken);
                var previousCount = results.Count;
                results.AddRange(templates
                    .Where(template => template.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.SearchDescription?.Contains(query, StringComparison.OrdinalIgnoreCase) is true)
                    .Select(template => new ConfiguredTemplate(name, template, configuredSource.DisplayName, source)));
                logger.LogDebug("Configured template source {SourceName} produced {MatchCount} search matches.", name, results.Count - previousCount);
            }
            catch (Exception ex) when (ex is TemplateDiscoveryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogDebug("Skipping failed configured template source {SourceName} and continuing the search ({FailureType}).", name, ex.GetType().Name);
                Warn(name, ex.Message);
            }
        }

        return results.OrderBy(result => result.Template.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.SourceName, StringComparers.TemplateSourceName)
            .ThenBy(result => result.Template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Warn(string source, string message) =>
        interactionService.DisplayMessage(KnownEmojis.PoliceCarLight, string.Format(
            CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, source, message));
}

internal sealed record ConfiguredTemplate(string SourceName, ITemplate Template, string? SourceDisplayName, TemplateSource Source);
