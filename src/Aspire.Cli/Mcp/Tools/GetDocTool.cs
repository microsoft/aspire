// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Mcp.Docs;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for retrieving a specific Aspire documentation page or section.
/// </summary>
internal sealed class GetDocTool(IDocsIndexService docsIndexService) : CliMcpTool
{
    private readonly IDocsIndexService _docsIndexService = docsIndexService;

    public override string Name => KnownMcpTools.GetDoc;

    public override string Description => """
        Retrieves the full content of a specific Aspire documentation page by its slug.
        Optionally filter to a specific section by providing a section heading.
        Use list_docs to browse available pages, or search_docs to find relevant content by keyword.
        """;

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "slug": {
                  "type": "string",
                  "description": "The slug of the documentation page to retrieve (e.g., 'redis-integration', 'getting-started')."
                },
                "section": {
                  "type": "string",
                  "description": "Optional. The heading of a specific section to return. If omitted, returns the entire page."
                }
              },
              "required": ["slug"],
              "additionalProperties": false,
              "description": "Retrieves documentation content by slug, optionally filtered to a specific section."
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;

        if (arguments is null || !arguments.TryGetValue("slug", out var slugElement))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'slug' parameter is required. Use list_docs to see available pages." }]
            };
        }

        var slug = slugElement.GetString();
        if (string.IsNullOrWhiteSpace(slug))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'slug' parameter cannot be empty." }]
            };
        }

        string? section = null;
        if (arguments.TryGetValue("section", out var sectionElement))
        {
            section = sectionElement.GetString();
        }

        await DocsToolHelper.EnsureIndexedWithNotificationsAsync(_docsIndexService, context.ProgressToken, context.Notifier, cancellationToken).ConfigureAwait(false);

        var doc = await _docsIndexService.GetDocumentAsync(slug, section, cancellationToken).ConfigureAwait(false);

        if (doc is null)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"No documentation found for slug '{slug}'. Use list_docs to see available pages." }]
            };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = doc.Content }]
        };
    }
}
