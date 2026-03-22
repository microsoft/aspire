// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp.Docs;

public class DocsSearchServiceTests
{
    private static DocsSearchService CreateService(IDocsIndexService indexService)
    {
        return new DocsSearchService(indexService, NullLogger<DocsSearchService>.Instance);
    }

    private static DocsIndexService CreateIndexService(IDocsFetcher? fetcher = null, IDocsCache? cache = null)
    {
        return new DocsIndexService(
            fetcher ?? new MockDocsFetcher(null),
            cache ?? new NullDocsCache(),
            NullLogger<DocsIndexService>.Instance);
    }

    [Fact]
    public async Task SearchAsync_ReturnsFormattedResponse()
    {
        var content = """
            # Redis Integration
            > Connect to Redis for caching.

            Redis content with details.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");

        Assert.NotNull(response);
        Assert.Equal("Redis", response.Query);
        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_WithNoResults_ReturnsEmptyResults()
    {
        var content = """
            # PostgreSQL Integration
            > Connect to PostgreSQL databases.

            PostgreSQL content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("nonexistent-term-xyz");

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task FormatAsMarkdown_WithResults_FormatsCorrectly()
    {
        var content = """
            # Redis Integration
            > Connect to Redis for caching.

            Redis content with details.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");
        Assert.NotNull(response);

        var markdown = response.FormatAsMarkdown("Test Results");

        Assert.Contains("# Test Results", markdown);
        Assert.Contains("## Redis Integration", markdown);
        Assert.Contains("**Slug:**", markdown);
    }

    [Fact]
    public async Task FormatAsMarkdown_WithScores_IncludesScores()
    {
        var content = """
            # Redis Integration
            > Connect to Redis for caching.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");
        Assert.NotNull(response);

        var markdown = response.FormatAsMarkdown(showScores: true);

        Assert.Contains("Score:", markdown);
    }

    [Fact]
    public async Task FormatAsMarkdown_NoResults_ReturnsHelpfulMessage()
    {
        var content = """
            # PostgreSQL Integration
            > Connect to PostgreSQL databases.

            PostgreSQL content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("xyz-not-found");
        Assert.NotNull(response);

        var markdown = response.FormatAsMarkdown();

        Assert.Contains("No results found", markdown);
        Assert.Contains("xyz-not-found", markdown);
    }

    [Fact]
    public async Task SearchAsync_RespectsTopKLimit()
    {
        var content = """
            # Redis Doc 1
            > Redis documentation part 1.

            Redis content here.

            # Redis Doc 2
            > Redis documentation part 2.

            More Redis content.

            # Redis Doc 3
            > Redis documentation part 3.

            Redis again here.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis", topK: 2);

        Assert.NotNull(response);
        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task SearchAsync_IncludesMatchedSection()
    {
        var content = """
            # Getting Started
            > Quick start guide.

            ## Redis Configuration
            Configure Redis connection strings.

            ## PostgreSQL Configuration
            Configure PostgreSQL.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");
        Assert.NotNull(response);
        Assert.NotEmpty(response.Results);

        var result = response.Results[0];
        Assert.NotNull(result.Section);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_WithInvalidQuery_ReturnsResponseWithEmptyResults(string? query)
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync(query!);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_WhenNoDocsAvailable_ReturnsResponseWithEmptyResults()
    {
        var fetcher = new MockDocsFetcher(null);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_WhenDocsEmpty_ReturnsResponseWithEmptyResults()
    {
        var fetcher = new MockDocsFetcher("");
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task FormatAsMarkdown_WithNullTitle_UsesDefaultTitle()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");
        Assert.NotNull(response);

        var markdown = response.FormatAsMarkdown(null);

        Assert.Contains("Documentation for:", markdown);
        Assert.Contains("Redis", markdown);
    }

    [Fact]
    public async Task FormatAsMarkdown_WithEmptyTitle_UsesEmptyTitle()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis");
        Assert.NotNull(response);

        var markdown = response.FormatAsMarkdown("");

        // Verify the content contains the document title and slug
        Assert.Contains("## Redis Integration", markdown);
        Assert.Contains("**Slug:**", markdown);
        Assert.Contains("Redis", markdown);
    }

    [Fact]
    public async Task SearchAsync_WithZeroTopK_ReturnsEmptyResults()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis", topK: 0);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_WithNegativeTopK_ReturnsEmptyResults()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis", topK: -5);

        Assert.NotNull(response);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task SearchAsync_WithLargeTopK_ReturnsAllAvailableResults()
    {
        var content = """
            # Redis Doc 1
            > Redis content 1.

            Redis info.

            # Redis Doc 2
            > Redis content 2.

            More Redis.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis", topK: 1000);

        Assert.NotNull(response);
        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task SearchAsync_WhenIndexerThrows_PropagatesException()
    {
        var fetcher = new ThrowingDocsFetcher(new InvalidOperationException("Index failed"));
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => searchService.SearchAsync("Redis"));
    }

    [Fact]
    public async Task SearchAsync_WithSpecialCharactersInQuery_HandlesGracefully()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Redis!@#$%^&*()");

        Assert.NotNull(response);
        // Should not throw, may or may not have results depending on tokenization
    }

    [Fact]
    public async Task SearchAsync_WithUnicodeCharacters_HandlesGracefully()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = new MockDocsFetcher(content);
        var indexService = CreateIndexService(fetcher);
        var searchService = CreateService(indexService);

        var response = await searchService.SearchAsync("Rédis 日本語 🚀");

        Assert.NotNull(response);
        // Should not throw
    }

    private sealed class MockDocsFetcher(string? content) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(content);
        }
    }

    private sealed class ThrowingDocsFetcher(Exception exception) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class NullDocsCache : IDocsCache
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default) => Task.FromResult<LlmsDocument[]?>(null);
        public Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
