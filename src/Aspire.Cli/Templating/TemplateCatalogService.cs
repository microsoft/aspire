// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Templating;

internal sealed class TemplateCatalogService(
    IHttpClientFactory httpClientFactory,
    ITemplateProvider templateProvider,
    CliExecutionContext executionContext,
    IInteractionService interactionService)
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<IEnumerable<ITemplate>> SearchAsync(
        string href, string settingPath, string query, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(nameof(TemplateCatalogService));
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = MaximumResponseBytes;
        var url = href.Replace("{search}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{cliVersion}", Uri.EscapeDataString(executionContext.IdentityVersion), StringComparison.Ordinal);
        try
        {
            // Buffering enforces both the byte limit (including chunked responses) and the
            // download timeout before parsing. Do not log request URLs or response contents.
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                    string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogHttpFailed, (int)response.StatusCode));
            }
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            // Static and searchable servers return the same envelope:
            // { "templates": [{ "name": "...", "description": "...", "repository": "https://host/repo",
            //                    "ref": "main", "path": "templates/api" }] }.
            if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("templates", out var entries) ||
                entries.ValueKind is not JsonValueKind.Array)
            {
                throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogInvalidResponse);
            }

            List<ITemplate> templates = [];
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = $"{settingPath}.templates[{index++}]";
                try
                {
                    if (entry.ValueKind is not JsonValueKind.Object)
                    {
                        throw new TemplateSourceConfigurationException(location, NewCommandStrings.TemplateCatalogEntryMustBeObject);
                    }
                    var name = TemplateSourceConfiguration.ReadRequiredString(entry, "name", location);
                    var description = TemplateSourceConfiguration.ReadRequiredString(entry, "description", location);
                    var repository = TemplateSourceConfiguration.ReadRequiredString(entry, "repository", location);
                    TemplateSourceConfiguration.ValidateRepository(repository, $"{location}.repository");
                    var reference = TemplateSourceConfiguration.ReadRequiredString(entry, "ref", location);
                    var path = TemplateSourceConfiguration.ReadRequiredString(entry, "path", location);
                    TemplateSourceConfiguration.ValidateRepositoryPath(path, $"{location}.path");
                    // A smart server may filter; a static host may return everything. Neither
                    // replaces client-side filtering, and listing never resolves or clones Git.
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        templates.Add(new CallbackTemplate(name, description,
                            (context, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, context.WorkingDirectory.FullName),
                            _ => { },
                            async (_, inputs, parseResult, token) =>
                            {
                                var discovered = (await templateProvider.GetTemplatesAsync(
                                    new TemplateSource(path, repository, reference), token)).ToArray();
                                if (discovered.Length != 1)
                                {
                                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                                        string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogRequiresSingleTemplate, name));
                                }
                                return await discovered[0].ApplyTemplateAsync(inputs, parseResult, token);
                            }));
                    }
                }
                catch (TemplateSourceConfigurationException ex)
                {
                    interactionService.DisplayMessage(KnownEmojis.Warning, ex.Message);
                }
            }
            return templates;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogTimedOut);
        }
        catch (HttpRequestException)
        {
            // Transport exceptions can contain private URLs, query text, or server diagnostics.
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogDownloadFailed);
        }
        catch (JsonException)
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogInvalidResponse);
        }
    }
}
