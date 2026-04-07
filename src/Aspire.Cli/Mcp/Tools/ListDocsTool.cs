// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Mcp.Docs;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for listing available Aspire documentation pages.
/// </summary>
internal sealed class ListDocsTool(IDocsIndexService docsIndexService) : CliMcpTool
{
    private readonly IDocsIndexService _docsIndexService = docsIndexService;

    public override string Name => KnownMcpTools.ListDocs;

    public override string Description => """
        Lists all available Aspire documentation pages from aspire.dev.
        Returns page titles, URL slugs, and brief summaries.
        Use this to browse the documentation catalog and discover available topics.
        Pass a slug to the get_doc tool to retrieve full page content.
        """;

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": false,
              "description": "Lists all available Aspire documentation pages. No parameters required."
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        await DocsToolHelper.EnsureIndexedWithNotificationsAsync(_docsIndexService, context.ProgressToken, context.Notifier, cancellationToken).ConfigureAwait(false);

        var docs = await _docsIndexService.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);

        if (docs.Count is 0)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No documentation available. The aspire.dev docs may not have loaded correctly." }]
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Aspire Documentation Pages");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {docs.Count} documentation pages:");
        sb.AppendLine();

        foreach (var doc in docs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {doc.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Slug:** `{doc.Slug}`");
            if (!string.IsNullOrEmpty(doc.Summary))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> {doc.Summary}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Use `get_doc` with a slug to retrieve the full content of a specific page.");

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = sb.ToString() }]
        };
    }
}
