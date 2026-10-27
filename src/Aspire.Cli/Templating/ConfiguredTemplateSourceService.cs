// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Templating;

internal sealed class ConfiguredTemplateSourceService(
    IConfigurationService configurationService,
    ITemplateProvider templateProvider,
    TemplateCatalogService catalogService,
    IInteractionService interactionService)
{
    internal async Task<IReadOnlyList<KeyValuePair<string, ConfiguredTemplateSource>>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var settingsPath = configurationService.GetSettingsFilePath(isGlobal: true);
        try
        {
            using var settings = JsonDocument.Parse(
                await File.ReadAllTextAsync(settingsPath, cancellationToken), ConfigurationHelper.ParseOptions);
            // Sources are local, Git, or catalog objects, e.g.
            // { "templates": { "local": { "path": "/absolute/path" },
            //   "remote": { "repository": "https://host/repo", "ref": "main", "path": "templates" },
            //   "catalog": { "href": "https://host/templates.json" } } }.
            // Read the JSON directly to preserve value types and avoid project-config overrides.
            if (settings.RootElement.ValueKind is not JsonValueKind.Object)
            {
                Warn(settingsPath, NewCommandStrings.TemplateSourcesMustBeObject);
                return [];
            }
            var templates = settings.RootElement.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("templates", StringComparison.OrdinalIgnoreCase)).Value;
            if (templates.ValueKind is JsonValueKind.Undefined)
            {
                return [];
            }
            if (templates.ValueKind is not JsonValueKind.Object)
            {
                Warn(settingsPath, NewCommandStrings.TemplateSourcesMustBeObject);
                return [];
            }

            List<KeyValuePair<string, ConfiguredTemplateSource>> sources = [];
            foreach (var entry in templates.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    TemplateSourceConfiguration.ValidateName(entry.Name);
                    var source = TemplateSourceConfiguration.Parse(entry.Value, $"templates.{entry.Name}", requireComplete: true);
                    sources.Add(new(entry.Name, source));
                }
                catch (TemplateSourceConfigurationException ex)
                {
                    Warn(entry.Name, ex.Message);
                }
            }

            return sources;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warn(settingsPath, ex.Message);
            return [];
        }
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
                IEnumerable<ITemplate> templates;
                if (configuredSource.Href is { } href)
                {
                    templates = await interactionService.ShowStatusAsync(status,
                        () => catalogService.SearchAsync(href, $"templates.{name}", query, cancellationToken));
                }
                else if (configuredSource.Template is { } source)
                {
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
                }
                else
                {
                    throw new InvalidOperationException("A complete template source must have a location.");
                }

                results.AddRange(templates
                    .Where(template => template.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        template.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(template => new ConfiguredTemplate(name, template)));
            }
            catch (Exception ex) when (ex is TemplateDiscoveryException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Warn(name, ex.Message);
            }
        }

        return results.OrderBy(result => result.Template.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Warn(string source, string message) =>
        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(
            CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSearchFailed, source, message));
}

internal sealed record ConfiguredTemplate(string SourceName, ITemplate Template);
