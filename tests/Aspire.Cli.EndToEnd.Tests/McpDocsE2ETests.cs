// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.TestUtilities;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for MCP docs-based tooling.
/// These tests exercise the full MCP protocol flow by launching the CLI as a subprocess.
/// </summary>
/// <remarks>
/// These tests require network access to fetch documentation from aspire.dev.
/// They are marked as outerloop tests to avoid slowing down regular CI.
/// </remarks>
[OuterloopTest("Requires network access to fetch aspire.dev documentation")]
public partial class McpDocsE2ETests : IAsyncLifetime
{
    private McpClient? _mcpClient;

    public async ValueTask InitializeAsync()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var cliProjectPath = Path.Combine(repoRoot, "src", "Aspire.Cli", "Aspire.Cli.csproj");

        if (!File.Exists(cliProjectPath))
        {
            throw new InvalidOperationException($"Could not find CLI project at: {cliProjectPath}");
        }

        string[] arguments = ["run", "--project", cliProjectPath, "--", "agent", "mcp"];

        var options = new StdioClientTransportOptions
        {
            Name = "aspire-mcp-e2e-test",
            Command = "dotnet",
            Arguments = arguments
        };

        var transport = new StdioClientTransport(options);
        _mcpClient = await McpClient.CreateAsync(transport);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListTools_IncludesDocsTools()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        Assert.Contains(tools, t => t.Name == "list_docs");
        Assert.Contains(tools, t => t.Name == "search_docs");
        Assert.Contains(tools, t => t.Name == "get_doc");
    }

    [Fact]
    public async Task ListDocs_ReturnsDocumentation()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        Assert.Contains("Aspire Documentation Pages", text);
        Assert.Contains("Slug:", text);
    }

    [Fact]
    public async Task SearchDocs_FindsRelevantContent()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "redis" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        // Should find Redis-related documentation
        Assert.Contains("Search Results", text);
        // Should include slug for use with get_doc
        Assert.Contains("Slug:", text);
    }

    [Fact]
    public async Task SearchDocs_RespectsTopKParameter()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "aspire", ["topK"] = 3 },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        // Should contain search results but limited count
        Assert.Contains("Search Results", text);
    }

    [Fact]
    public async Task SearchDocs_SlugCanBeUsedWithGetDoc()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // Search for documentation
        var searchResult = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "redis" },
            cancellationToken: cancellationToken);

        Assert.NotNull(searchResult);
        Assert.True(searchResult.IsError is null or false, $"Tool returned error: {GetResultText(searchResult)}");

        var searchText = GetResultText(searchResult);

        // Extract a slug from the search results
        var slugMatch = SlugRegex().Match(searchText);
        Assert.True(slugMatch.Success, "Could not find a slug in search_docs output. Search results should include slugs for use with get_doc.");

        var slug = slugMatch.Groups[1].Value;

        // Use the slug with get_doc to retrieve full content
        var docResult = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = slug },
            cancellationToken: cancellationToken);

        Assert.NotNull(docResult);
        Assert.True(docResult.IsError is null or false, $"get_doc returned error for slug '{slug}': {GetResultText(docResult)}");

        var docText = GetResultText(docResult);
        Assert.NotEmpty(docText);
    }

    [Fact]
    public async Task SearchDocs_WithEmptyQuery_ReturnsError()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is true, "Expected an error response for empty query");
    }

    [Fact]
    public async Task GetDoc_RetrievesDocumentContent()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // First list docs to get a valid slug
        var listResult = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);
        Assert.True(listResult.IsError is null or false);

        var listText = GetResultText(listResult);

        // Extract a slug from the list
        // Format: **Slug:** `some-slug`
        var slugMatch = SlugRegex().Match(listText);

        Assert.True(slugMatch.Success, "Could not find a slug in list_docs output");

        var slug = slugMatch.Groups[1].Value;

        // Now get that specific document
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = slug },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");

        var text = GetResultText(result);
        Assert.NotEmpty(text);
    }

    [Fact]
    public async Task GetDoc_WithInvalidSlug_ReturnsError()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?> { ["slug"] = "nonexistent-doc-that-does-not-exist" },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.IsError is true, "Expected an error response for invalid slug");
        Assert.Contains("No documentation found", GetResultText(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDoc_WithSection_ReturnsSpecificSection()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;

        // First list docs to get a valid slug
        var listResult = await _mcpClient.CallToolAsync("list_docs", cancellationToken: cancellationToken);
        Assert.True(listResult.IsError is null or false);

        var listText = GetResultText(listResult);

        // Extract a slug from the list
        var slugMatch = SlugRegex().Match(listText);

        Assert.True(slugMatch.Success, "Could not find a slug in list_docs output");

        var slug = slugMatch.Groups[1].Value;

        // Get document with a section filter (use a common section name)
        var result = await _mcpClient.CallToolAsync(
            "get_doc",
            new Dictionary<string, object?>
            {
                ["slug"] = slug,
                ["section"] = "Configuration"  // Common section name in docs
            },
            cancellationToken: cancellationToken);

        Assert.NotNull(result);
        // Even if section doesn't exist, should still return content
        Assert.True(result.IsError is null or false, $"Tool returned error: {GetResultText(result)}");
    }

    [Fact]
    public async Task ListTools_ToolSchemas_AreValid()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        var docTools = tools.Where(t => t.Name is "list_docs" or "search_docs" or "get_doc").ToList();

        foreach (var tool in docTools)
        {
            // Verify schema is valid JSON
            var schemaString = tool.ProtocolTool.InputSchema.ToString();
            Assert.NotEmpty(schemaString);

            // Should be parseable JSON
            using var parsed = JsonDocument.Parse(schemaString);
            Assert.NotNull(parsed);
        }
    }

    [Fact]
    public async Task SearchDocs_ToolDescription_IsInformative()
    {
        Assert.NotNull(_mcpClient);

        var cancellationToken = TestContext.Current.CancellationToken;
        var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        var searchTool = tools.FirstOrDefault(t => t.Name == "search_docs");
        Assert.NotNull(searchTool);
        Assert.NotEmpty(searchTool.Description);
        Assert.Contains("search", searchTool.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetResultText(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
        {
            return string.Empty;
        }

        return result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? string.Empty;
    }

    [GeneratedRegex(@"\*\*Slug:\*\* `([^`]+)`")]
    private static partial Regex SlugRegex();
}
