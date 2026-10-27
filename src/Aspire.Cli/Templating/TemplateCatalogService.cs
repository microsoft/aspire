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
    internal const int MaximumPages = 10;

    public async Task<IEnumerable<ITemplate>> SearchAsync(
        string href, string settingPath, string query, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(nameof(TemplateCatalogService));
        client.Timeout = TimeSpan.FromSeconds(30);
        var url = href.Replace("{search}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{cliVersion}", Uri.EscapeDataString(executionContext.IdentityVersion), StringComparison.Ordinal);
        var origin = new Uri(url);
        var next = origin;
        HashSet<Uri> visited = [];
        List<ITemplate> templates = [];
        var totalBytes = 0;
        // The deadline and byte budget cover the whole search, not each individual page.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            for (var page = 1; ; page++)
            {
                visited.Add(next);
                // Buffering also bounds chunked responses. Never log URLs, continuation tokens,
                // or response bodies: they can contain search text or private server diagnostics.
                using var response = await client.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                        string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogHttpFailed, (int)response.StatusCode));
                }
                await response.Content.LoadIntoBufferAsync(MaximumResponseBytes - totalBytes, token);
                var content = await response.Content.ReadAsByteArrayAsync(token);
                totalBytes += content.Length;
                using var document = JsonDocument.Parse(content);
                // Static and searchable servers share this shape. Missing/null nextLink ends paging:
                // { "templates": [{ "name": "...", "description": "...", "repository": "https://host/repo",
                //   "ref": "main", "path": "templates/api" }], "nextLink": "?cursor=opaque%2Btoken" }.
                var root = document.RootElement;
                if (root.ValueKind is not JsonValueKind.Object ||
                    !root.TryGetProperty("templates", out var entries) ||
                    entries.ValueKind is not JsonValueKind.Array)
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogInvalidResponse);
                }
                AddTemplates(entries, templates, settingPath, query, token);

                if (!root.TryGetProperty("nextLink", out var continuation) || continuation.ValueKind is JsonValueKind.Null)
                {
                    token.ThrowIfCancellationRequested();
                    return templates;
                }
                if (continuation.ValueKind is not JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(continuation.GetString()) ||
                    !Uri.TryCreate(next, continuation.GetString(), out var resolved) ||
                    resolved.Scheme != Uri.UriSchemeHttps ||
                    !resolved.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                    resolved.Port != origin.Port || resolved.UserInfo.Length != 0 || resolved.Fragment.Length != 0 ||
                    visited.Contains(resolved))
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateCatalogInvalidContinuation);
                }
                if (page == MaximumPages)
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                        string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateCatalogPageLimit, MaximumPages));
                }
                next = resolved;
            }
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

    private void AddTemplates(JsonElement entries, List<ITemplate> templates, string settingPath, string query, CancellationToken cancellationToken)
    {
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
    }
}
