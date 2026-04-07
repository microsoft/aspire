// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp.Docs;

public class DocsIndexServiceTests
{
    private static IDocsFetcher CreateMockFetcher(string? content)
    {
        return new MockDocsFetcher(content);
    }

    private static DocsIndexService CreateService(IDocsFetcher? fetcher = null, IDocsCache? cache = null)
    {
        return new DocsIndexService(
            fetcher ?? new MockDocsFetcher(null),
            cache ?? new NullDocsCache(),
            NullLogger<DocsIndexService>.Instance);
    }

    [Fact]
    public async Task ListDocumentsAsync_ReturnsAllDocuments()
    {
        var content = """
            # Redis Integration
            > Connect to Redis for caching.

            Redis content.

            # PostgreSQL Integration
            > Connect to PostgreSQL databases.

            PostgreSQL content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var docs = await service.ListDocumentsAsync();

        Assert.Equal(2, docs.Count);
        Assert.Contains(docs, d => d.Title == "Redis Integration");
        Assert.Contains(docs, d => d.Title == "PostgreSQL Integration");
    }

    [Fact]
    public async Task ListDocumentsAsync_WhenFetchFails_ReturnsEmptyList()
    {
        var fetcher = CreateMockFetcher(null);
        var service = CreateService(fetcher);

        var docs = await service.ListDocumentsAsync();

        Assert.Empty(docs);
    }

    [Fact]
    public async Task SearchAsync_FindsDocumentByTitle()
    {
        var content = """
            # Redis Integration
            > Connect to Redis for caching.

            Redis content.

            # PostgreSQL Integration
            > Connect to PostgreSQL databases.

            PostgreSQL content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis");

        Assert.NotEmpty(results);
        Assert.Equal("Redis Integration", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_FindsDocumentBySummary()
    {
        var content = """
            # Integration Guide
            > Learn how to connect Redis caching to your app.

            Some content here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("caching");

        Assert.NotEmpty(results);
        Assert.Equal("Integration Guide", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_FindsDocumentBySectionHeading()
    {
        var content = """
            # Getting Started
            > Quick start guide.

            ## Configuration Options
            Configure the app using environment variables.

            ## Deployment Steps
            Deploy to Azure Container Apps.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Configuration");

        Assert.NotEmpty(results);
        Assert.Equal("Getting Started", results[0].Title);
        Assert.Equal("Configuration Options", results[0].MatchedSection);
    }

    [Fact]
    public async Task SearchAsync_TitleMatchScoresHigherThanBodyMatch()
    {
        var content = """
            # Redis Overview
            > Official Redis documentation.

            This document covers Redis basics and setup.

            # Database Overview
            > Learn about databases.

            PostgreSQL and MySQL are popular database options. Redis is sometimes mentioned.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis");

        Assert.NotEmpty(results);
        // Document with "Redis" in title should rank higher
        Assert.Equal("Redis Overview", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_FindsCodeIdentifiers()
    {
        var content = """
            # Redis Integration
            > Add Redis to your app.

            ## Usage

            ```csharp
            var redis = builder.AddRedis("cache");
            ```

            Call `AddRedis` to add a Redis resource.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("AddRedis");

        Assert.NotEmpty(results);
        Assert.Equal("Redis Integration", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_RespectsTopKLimit()
    {
        var content = """
            # Doc 1
            > Redis documentation.

            Redis content here.

            # Doc 2
            > More Redis info.

            Redis info here.

            # Doc 3
            > Yet more Redis.

            More Redis content.

            # Doc 4
            > Redis again.

            Redis again here.

            # Doc 5
            > Redis everywhere.

            Redis everywhere here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis", topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task SearchAsync_WithEmptyQuery_ReturnsEmptyResults()
    {
        var content = """
            # Some Document
            Content here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithWhitespaceQuery_ReturnsEmptyResults()
    {
        var content = """
            # Some Document
            Content here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("   ");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MultiWordQuery_FindsAllTerms()
    {
        var content = """
            # Redis Caching Guide
            > How to use Redis for caching.

            Implement distributed caching with Redis.

            # Memory Caching
            > In-memory caching without Redis.

            Simple memory cache implementation.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis caching");

        Assert.NotEmpty(results);
        // Document with both terms should rank highest
        Assert.Equal("Redis Caching Guide", results[0].Title);
    }

    [Fact]
    public async Task GetDocumentAsync_BySlug_ReturnsDocument()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("redis-integration");

        Assert.NotNull(doc);
        Assert.Equal("Redis Integration", doc.Title);
        Assert.Equal("redis-integration", doc.Slug);
    }

    [Fact]
    public async Task GetDocumentAsync_CaseInsensitive()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("REDIS-INTEGRATION");

        Assert.NotNull(doc);
        Assert.Equal("Redis Integration", doc.Title);
    }

    [Fact]
    public async Task GetDocumentAsync_UnknownSlug_ReturnsNull()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("nonexistent-doc");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetDocumentAsync_WithSection_ReturnsOnlySection()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Main content.

            ## Installation
            Install via NuGet.

            ## Configuration
            Configure connection strings.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("redis-integration", "Installation");

        Assert.NotNull(doc);
        Assert.Contains("Install via NuGet", doc.Content);
        Assert.DoesNotContain("Configure connection strings", doc.Content);
    }

    [Fact]
    public async Task GetDocumentAsync_WithPartialSectionName_FindsSection()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            ## Getting Started with Redis
            Quick start content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("redis-integration", "Getting Started");

        Assert.NotNull(doc);
        Assert.Contains("Quick start content", doc.Content);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsSectionsList()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            ## Installation
            Install content.

            ## Configuration
            Config content.

            ## Usage
            Usage content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("redis-integration");

        Assert.NotNull(doc);
        Assert.Equal(3, doc.Sections.Count);
        Assert.Contains("Installation", doc.Sections);
        Assert.Contains("Configuration", doc.Sections);
        Assert.Contains("Usage", doc.Sections);
    }

    [Fact]
    public async Task EnsureIndexedAsync_OnlyFetchesOnce()
    {
        var callCount = 0;
        var fetcher = new CountingDocsFetcher(() =>
        {
            callCount++;
            return "# Doc\nContent.";
        });
        var service = CreateService(fetcher);

        await service.EnsureIndexedAsync();
        await service.EnsureIndexedAsync();
        await service.EnsureIndexedAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task SearchAsync_OrdersResultsByScore()
    {
        var content = """
            # Redis Quick Start
            > Get started with Redis in minutes.

            ## Installation
            Install Redis.

            # Advanced Redis Patterns
            > Deep dive into Redis patterns and best practices.

            ## Redis Pub/Sub
            Learn about Redis publish/subscribe.

            ## Redis Clustering
            Configure Redis clustering for high availability.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis");

        // All results should have scores in descending order
        for (var i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Score >= results[i].Score,
                $"Results not in descending score order: {results[i - 1].Score} < {results[i].Score}");
        }
    }

    [Fact]
    public async Task SearchAsync_WithNullQuery_ReturnsEmptyResults()
    {
        var content = """
            # Some Document
            > Summary here.

            Content here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync(null!);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDocumentAsync_WithNullSlug_ReturnsNull()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync(null!);

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetDocumentAsync_WithEmptySlug_ReturnsNull()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("");

        Assert.Null(doc);
    }

    [Fact]
    public async Task GetDocumentAsync_WithWhitespaceSlug_ReturnsNull()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("   ");

        Assert.Null(doc);
    }

    [Fact]
    public async Task ListDocumentsAsync_WhenFetchReturnsEmpty_ReturnsEmptyList()
    {
        var fetcher = CreateMockFetcher("");
        var service = CreateService(fetcher);

        var docs = await service.ListDocumentsAsync();

        Assert.Empty(docs);
    }

    [Fact]
    public async Task ListDocumentsAsync_WhenFetchReturnsWhitespace_ReturnsEmptyList()
    {
        var fetcher = CreateMockFetcher("   \n\t\n   ");
        var service = CreateService(fetcher);

        var docs = await service.ListDocumentsAsync();

        Assert.Empty(docs);
    }

    [Fact]
    public async Task SearchAsync_WhenNoDocsIndexed_ReturnsEmptyResults()
    {
        var fetcher = CreateMockFetcher(null);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetDocumentAsync_WhenNoDocsIndexed_ReturnsNull()
    {
        var fetcher = CreateMockFetcher(null);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("any-slug");

        Assert.Null(doc);
    }

    [Fact]
    public async Task ListDocumentsAsync_WhenFetcherThrows_PropagatesException()
    {
        var fetcher = new ThrowingDocsFetcher(new InvalidOperationException("Fetch failed"));
        var service = CreateService(fetcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListDocumentsAsync().AsTask());
    }

    [Fact]
    public async Task SearchAsync_WhenFetcherThrows_PropagatesException()
    {
        var fetcher = new ThrowingDocsFetcher(new HttpRequestException("Network error"));
        var service = CreateService(fetcher);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("Redis").AsTask());
    }

    [Fact]
    public async Task GetDocumentAsync_WhenFetcherThrows_PropagatesException()
    {
        var fetcher = new ThrowingDocsFetcher(new TimeoutException("Request timed out"));
        var service = CreateService(fetcher);

        await Assert.ThrowsAsync<TimeoutException>(() => service.GetDocumentAsync("redis-integration").AsTask());
    }

    [Fact]
    public async Task EnsureIndexedAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var fetcher = new DelayingDocsFetcher("# Doc\nContent.", TimeSpan.FromSeconds(10));
        var service = CreateService(fetcher);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnsureIndexedAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task EnsureIndexedAsync_WhenFetcherThrows_PropagatesException()
    {
        var fetcher = new ThrowingDocsFetcher(new InvalidOperationException("Critical error"));
        var service = CreateService(fetcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureIndexedAsync().AsTask());
    }

    [Fact]
    public async Task SearchAsync_WithSpecialCharactersInQuery_HandlesGracefully()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        // Should not throw
        var results = await service.SearchAsync("Redis!@#$%^&*()");

        // May or may not find results, but should not throw
        Assert.NotNull(results);
    }

    [Fact]
    public async Task SearchAsync_WithVeryLongQuery_HandlesGracefully()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var longQuery = new string('a', 10000);

        // Should not throw
        var results = await service.SearchAsync(longQuery);

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetDocumentAsync_WithNonExistentSection_ReturnsFullDocument()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Main content here.

            ## Installation
            Install content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var doc = await service.GetDocumentAsync("redis-integration", "NonExistentSection");

        Assert.NotNull(doc);
        // When section not found, returns full content
        Assert.Contains("Main content here", doc.Content);
    }

    [Fact]
    public async Task SearchAsync_WithZeroTopK_ReturnsEmptyResults()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis", topK: 0);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithNegativeTopK_ReturnsEmptyResults()
    {
        var content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("Redis", topK: -1);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_SlugExactMatch_RanksHigher()
    {
        // This tests the "service discovery" example from the issue
        // Query "service-discovery" should match slug "service-discovery" and rank #1
        var content = """
            # Service Discovery
            > Learn about service discovery in Aspire.

            Service discovery content.

            # Azure Service Bus
            > Connect to Azure Service Bus.

            Azure Service Bus has a service name.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("service-discovery");

        Assert.NotEmpty(results);
        Assert.Equal("Service Discovery", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_SlugPhraseMatch_RanksHigher()
    {
        // Query "service discovery" should match slug "service-discovery" with high score
        // and not "azure-service-bus" just because "service" appears in it
        var content = """
            # Service Discovery
            > Learn about service discovery in Aspire.

            Service discovery content.

            # Azure Service Bus
            > Connect to Azure Service Bus for messaging.

            Azure Service Bus documentation with lots of service mentions.
            Service is mentioned multiple times. Service again. And service.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("service discovery");

        Assert.NotEmpty(results);
        Assert.Equal("Service Discovery", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_WhatsNewPenalty_RanksLower()
    {
        // "What's New" pages mention many features and should rank lower than dedicated docs
        var content = """
            # JavaScript Integration
            > How to use JavaScript with Aspire.

            JavaScript integration details.

            # What's New in Aspire 1.3
            > Release notes for Aspire 1.3.

            JavaScript support was added. JavaScript is now fully supported.
            JavaScript JavaScript JavaScript. We love JavaScript!
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("javascript");

        Assert.NotEmpty(results);
        // The dedicated JavaScript doc should rank higher even though What's New mentions it more
        Assert.Equal("JavaScript Integration", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_PartialSlugMatch_StillRanksReasonably()
    {
        // Query with partial slug match should still rank well
        var content = """
            # Configure the MCP Server
            > How to configure MCP.

            MCP configuration details.

            # Aspire Dashboard Configuration
            > Dashboard configuration including MCP settings.

            The dashboard has MCP options in settings.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("mcp");

        Assert.NotEmpty(results);
        // The doc with "mcp" in the slug should rank higher
        Assert.Equal("Configure the MCP Server", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_ChangelogPenalty_AppliesCorrectly()
    {
        // Similar to whats-new, changelog pages should be penalized
        var content = """
            # Redis Integration
            > How to use Redis with Aspire.

            Redis integration details.

            # Changelog
            > Complete changelog for Aspire.

            Redis support was added. Redis improvements. More Redis features.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("redis");

        Assert.NotEmpty(results);
        // The dedicated Redis doc should rank higher than the changelog
        Assert.Equal("Redis Integration", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_MultiWordQuery_MatchesSlugSegments()
    {
        // Query "azure cosmos" should match slug "azure-cosmos-db" well
        var content = """
            # Azure Cosmos DB
            > Connect to Azure Cosmos DB.

            Cosmos content.

            # Azure Overview
            > General Azure services overview.

            Overview includes Cosmos DB mention.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("azure cosmos");

        Assert.NotEmpty(results);
        Assert.Equal("Azure Cosmos DB", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_SingleWordQuery_UsesSegmentMatching()
    {
        // Single-word query should use segment-based matching (10 points)
        // not phrase matching (30 points).
        // This ensures "service" is scored by segment matches so that docs with "service"
        // in the title and slug outrank docs where it only appears in the body.
        var content = """
            # Redis Integration
            > How to use Redis with Aspire.

            Redis integration details.

            # Azure Service Bus
            > Connect to Azure Service Bus.

            The service is for messaging. Redis is mentioned in the service docs.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("service");

        Assert.NotEmpty(results);
        // Both docs should return results, but Azure Service Bus should rank higher
        // because "service" is in the title AND as a slug segment
        Assert.Equal("Azure Service Bus", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_HyphenatedQuery_MatchesSlugWithExtraSegments()
    {
        // Query "service-bus" should match slug "azure-service-bus" 
        // even though it's a single token containing a hyphen
        var content = """
            # Azure Service Bus
            > Connect to Azure Service Bus.

            Service Bus content.

            # Azure Overview
            > General Azure services overview.

            Overview of Azure services.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("service-bus");

        Assert.NotEmpty(results);
        Assert.Equal("Azure Service Bus", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_ChangelogQuery_DoesNotApplyPenalty()
    {
        // When user searches for "changelog", the changelog page should NOT be penalized
        var content = """
            # Changelog
            > Complete changelog for Aspire.

            Version 1.0 changes. Version 2.0 changes.

            # Some Other Page
            > Random page.

            Changelog mentioned once.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("changelog");

        Assert.NotEmpty(results);
        // The dedicated Changelog page should rank highest when user searches for it
        Assert.Equal("Changelog", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_WhatsNewQuery_DoesNotApplyPenalty()
    {
        // When user searches for "whats new", the whats-new page should NOT be penalized
        var content = """
            # What's New in Aspire 1.3
            > Release notes for Aspire 1.3.

            New features and improvements.

            # Other Documentation
            > Some other docs.

            Nothing new here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("whats new");

        Assert.NotEmpty(results);
        // The What's New page should rank highest when user searches for it
        Assert.Equal("What's New in Aspire 1.3", results[0].Title);
    }

    private sealed class MockDocsFetcher(string? content) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(content);
        }
    }

    private sealed class CountingDocsFetcher(Func<string?> contentProvider) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(contentProvider());
        }
    }

    private sealed class ThrowingDocsFetcher(Exception exception) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class DelayingDocsFetcher(string? content, TimeSpan delay) : IDocsFetcher
    {
        public async Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return content;
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
