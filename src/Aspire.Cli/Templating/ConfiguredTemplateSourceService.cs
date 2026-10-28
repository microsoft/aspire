// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Templating;

internal sealed class ConfiguredTemplateSourceService(
    IConfigurationService configurationService,
    ITemplateProvider templateProvider,
    IInteractionService interactionService)
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
            return [];
        }

        List<KeyValuePair<string, ConfiguredTemplateSource>> sources = [];
        foreach (var (name, definition) in settings?.TemplateSources ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateSourceConfiguration.ValidateName(name);
            if (definition is null)
            {
                throw new TemplateSourceConfigurationException($"templateSources.{name}", NewCommandStrings.InvalidConfiguredTemplateSource);
            }
            var source = TemplateSourceConfiguration.Parse(
                definition.Path, definition.Repository, definition.Ref, $"templateSources.{name}", requireComplete: true);
            sources.Add(new(name, source));
        }

        return sources;
    }

    public async Task<IReadOnlyList<ConfiguredTemplate>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, NewCommandStrings.NoConfiguredTemplateSources);
            return [];
        }

        List<ConfiguredTemplate> results = [];
        // The template engine owns mutable mount/resource state. Discover sequentially rather
        // than sharing a factory across concurrent source installations.
        foreach (var (name, configuredSource) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = string.Format(CultureInfo.CurrentCulture, NewCommandStrings.SearchingTemplateSource, name);
                var source = configuredSource.Template
                    ?? throw new InvalidOperationException("A complete template source must have a location.");
                IEnumerable<ITemplate> templates;
                if (source.Repository is null)
                {
                    templates = await interactionService.ShowStatusAsync(status,
                        () => templateProvider.GetTemplatesAsync(source, cancellationToken));
                }
                else
                {
                    // Git owns its resolve/clone spinners; an enclosing status hides them.
                    interactionService.DisplaySubtleMessage(status);
                    templates = await templateProvider.GetTemplatesAsync(source, cancellationToken);
                }

                results.AddRange(templates
                    .Where(template => template.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.SearchDescription?.Contains(query, StringComparison.OrdinalIgnoreCase) is true)
                    .Select(template => new ConfiguredTemplate(name, template)));
            }
            catch (Exception ex) when (ex is TemplateDiscoveryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Warn(name, ex.Message);
            }
        }

        return results.OrderBy(result => result.Template.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.SourceName, StringComparers.TemplateSourceName)
            .ThenBy(result => result.Template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Warn(string source, string message) =>
        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(
            CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, source, message));
}

internal sealed record ConfiguredTemplate(string SourceName, ITemplate Template);
