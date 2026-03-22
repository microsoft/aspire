// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Mcp.Docs;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for searching aspire.dev documentation using lexical search.
/// </summary>
internal sealed class SearchDocsTool(IDocsSearchService docsSearchService, IDocsIndexService docsIndexService) : CliMcpTool
{
    private readonly IDocsSearchService _docsSearchService = docsSearchService;
    private readonly IDocsIndexService _docsIndexService = docsIndexService;

    public override string Name => KnownMcpTools.SearchDocs;

    public override string Description => """
        Searches the aspire.dev documentation using keyword-based lexical search.
        Returns ranked results based on weighted matching across titles, headings, code identifiers, and body text.
        Use this tool to find specific information about Aspire features, APIs, integrations, or concepts.
        For best results, include relevant terms like API names, configuration keys, or feature names.
        """;

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "The search query. Use natural language to describe what you're looking for."
                },
                "topK": {
                  "type": "integer",
                  "description": "The number of results to return (default: 5, max: 10).",
                  "minimum": 1,
                  "maximum": 10
                }
              },
              "required": ["query"],
              "additionalProperties": false,
              "description": "Searches aspire.dev documentation for relevant content."
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;
        if (arguments is null || !arguments.TryGetValue("query", out var queryElement))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'query' parameter is required." }]
            };
        }

        var query = queryElement.GetString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'query' parameter cannot be empty." }]
            };
        }

        var topK = 5;
        if (arguments.TryGetValue("topK", out var topKElement) && topKElement.TryGetInt32(out var topKValue))
        {
            topK = Math.Clamp(topKValue, 1, 10);
        }

        await DocsToolHelper.EnsureIndexedWithNotificationsAsync(_docsIndexService, context.ProgressToken, context.Notifier, cancellationToken).ConfigureAwait(false);

        var response = await _docsSearchService.SearchAsync(query, topK, cancellationToken);

        if (response is null)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Failed to fetch aspire.dev documentation for search. Please try again later." }]
            };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = response.FormatAsMarkdown($"Search Results for: \"{query}\"", showScores: true) }]
        };
    }
}
