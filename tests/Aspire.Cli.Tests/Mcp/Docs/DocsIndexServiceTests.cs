// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.Docs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Documentation.Docs;

public class DocsIndexServiceTests
{
    private static IDocsFetcher CreateMockFetcher(string? content)
    {
        return new MockDocsFetcher(content);
    }

    private static DocsIndexService CreateService(IDocsFetcher? fetcher = null, IDocsCache? cache = null, IConfiguration? configuration = null)
    {
        return new DocsIndexService(
            fetcher ?? new MockDocsFetcher(null),
            cache ?? new NullDocsCache(),
            configuration ?? new ConfigurationBuilder().Build(),
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
    public async Task SearchAsync_PreFilterHaystack_CoversEveryScorableField()
    {
        // Regression guard for the AllSearchableTextLower reject-filter invariant
        // declared in IndexedDocument's ctor: the per-doc haystack used by SearchAsync
        // to early-reject zero-score candidates must be a superset of every field
        // ScoreDocument can produce a positive score against. If a future change adds
        // (or removes) a scorable field without keeping the haystack in sync, docs
        // that match ONLY on that field will be silently filtered out before scoring
        // and never reach the result list — with no other test failing.
        //
        // We build a doc that places a unique sentinel token in each scorable field
        // and assert SearchAsync returns the doc for every sentinel. If any sentinel
        // returns empty, the corresponding field has been omitted from the haystack.
        var content = """
            # Sentineltitleword Integration
            > A summary that mentions sentinelsummaryword uniquely.

            ## Sentinelheadingword Overview

            Content body containing sentinelcontentword for verification.
            Inline code contains `sentinelcodespan`, and SentinelIdentifier is a PascalCase symbol.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        // Sentinel name -> field it appears in (for clearer failure messages).
        var sentinels = new (string Token, string Field)[]
        {
            ("sentineltitleword", "title (and derived slug)"),
            ("sentinelsummaryword", "summary"),
            ("sentinelheadingword", "section heading"),
            ("sentinelcontentword", "section content"),
            ("sentinelcodespan", "code span"),
            ("sentinelidentifier", "PascalCase identifier"),
        };

        foreach (var (token, field) in sentinels)
        {
            var results = await service.SearchAsync(token);
            Assert.True(
                results.Count > 0,
                $"SearchAsync('{token}') returned no results — the IndexedDocument haystack used by " +
                $"the early-reject filter is probably missing the {field} field. Update the " +
                "IndexedDocument constructor to include that field in AllSearchableTextLower.");
        }
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
    public async Task GetDocumentAsync_NormalizesInlineMarkdownAndRewritesLinks()
    {
        var content = """
            # Certificate configuration
            > Learn how to configure HTTPS endpoints and certificate trust for resources in Aspire to enable secure communication.

            Aspire provides two complementary sets of certificate APIs: 1. **HTTPS endpoint APIs**: Configure the certificates that resources use for their own HTTPS endpoints. 2. **Certificate trust APIs**: Configure which certificates resources trust when making outbound HTTPS connections. ### Why HTTPS matters [Section titled "Why HTTPS matters"](#why-https-matters) HTTPS is essential for protecting the security and privacy of data transmitted between services. See [Aspire CLI](/get-started/install-cli/) and [Why HTTPS matters](#why-https-matters). Trust the development certificate ```bash aspire certs trust ``` ## Next steps [Section titled "Next steps"](#next-steps) Continue here.
            """;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DocsSourceConfiguration.LlmsTxtUrlConfigPath] = "http://localhost:4321/llms-small.txt"
            })
            .Build();

        var service = CreateService(CreateMockFetcher(content), configuration: configuration);

        var doc = await service.GetDocumentAsync("certificate-configuration");

        Assert.NotNull(doc);
        Assert.Contains("\n1. **HTTPS endpoint APIs**", doc.Content, StringComparison.Ordinal);
        Assert.Contains("\n2. **Certificate trust APIs**", doc.Content, StringComparison.Ordinal);
        Assert.Contains("### Why HTTPS matters\n\nHTTPS is essential", doc.Content, StringComparison.Ordinal);
        Assert.Contains("```bash\naspire certs trust\n```", doc.Content, StringComparison.Ordinal);
        Assert.Contains("[Aspire CLI](http://localhost:4321/get-started/install-cli/)", doc.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Section titled", doc.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("](#why-https-matters)", doc.Content, StringComparison.Ordinal);
        Assert.Contains("See [Aspire CLI](http://localhost:4321/get-started/install-cli/) and Why HTTPS matters.", doc.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDocumentAsync_KeepsMinifiedSingleLineCodeBlocksOnSingleLine()
    {
        var content = """
            # Certificate configuration

            Example:
            ```csharp var builder = DistributedApplication.CreateBuilder(args); // Disable all automatic certificate configuration builder.AddPythonModule("api", "./api", "uvicorn") .WithoutHttpsCertificate() // No server cert config .WithCertificateTrustScope(CertificateTrustScope.None); // No client trust config builder.Build().Run(); ```
            """;

        var service = CreateService(CreateMockFetcher(content));

        var doc = await service.GetDocumentAsync("certificate-configuration");

        Assert.NotNull(doc);
        Assert.Contains(
            """
            ```csharp
            var builder = DistributedApplication.CreateBuilder(args); // Disable all automatic certificate configuration builder.AddPythonModule("api", "./api", "uvicorn") .WithoutHttpsCertificate() // No server cert config .WithCertificateTrustScope(CertificateTrustScope.None); // No client trust config builder.Build().Run();
            ```
            """.Replace("\r\n", "\n"),
            doc.Content,
            StringComparison.Ordinal);
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
    public async Task EnsureIndexedAsync_RevalidatesCachedIndexAcrossInstances()
    {
        var cache = new MemoryDocsCache();
        const string content = """
            # Redis Integration
            > Connect to Redis.

            Redis content.
            """;

        var firstService = CreateService(
            CreateMockFetcher(content),
            cache);

        await firstService.EnsureIndexedAsync();

        var fetchCount = 0;
        var secondService = CreateService(
            new CountingDocsFetcher(() =>
            {
                fetchCount++;
                return content;
            }),
            cache);

        var docs = await secondService.ListDocumentsAsync();

        var doc = Assert.Single(docs);
        Assert.Equal("Redis Integration", doc.Title);
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task EnsureIndexedAsync_UsesCachedIndexWhenSourceUnavailableAfterInitialLoad()
    {
        var cache = new MemoryDocsCache();
        var firstService = CreateService(
            CreateMockFetcher(
                """
                # Redis Integration
                > Connect to Redis.

                Redis content.
                """),
            cache);

        await firstService.EnsureIndexedAsync();

        var secondService = CreateService(CreateMockFetcher(null), cache);
        var docs = await secondService.ListDocumentsAsync();

        var doc = Assert.Single(docs);
        Assert.Equal("Redis Integration", doc.Title);
    }

    [Fact]
    public async Task EnsureIndexedAsync_RefreshesCachedIndexWhenSourceContentChanges()
    {
        var cache = new MemoryDocsCache();
        var firstService = CreateService(
            CreateMockFetcher(
                """
                # Redis Integration
                > Connect to Redis.

                Redis content.
                """),
            cache);

        await firstService.EnsureIndexedAsync();

        var secondService = CreateService(
            CreateMockFetcher(
                """
                # PostgreSQL Integration
                > Connect to PostgreSQL.

                PostgreSQL content.
                """),
            cache);

        var docs = await secondService.ListDocumentsAsync();

        var doc = Assert.Single(docs);
        Assert.Equal("PostgreSQL Integration", doc.Title);

        var thirdService = CreateService(CreateMockFetcher(null), cache);
        var cachedDocs = await thirdService.ListDocumentsAsync();

        Assert.Equal("PostgreSQL Integration", Assert.Single(cachedDocs).Title);
    }

    [Fact]
    public async Task GetDocumentAsync_NormalizesMinifiedInlineTables()
    {
        var content = """
            # Deploy to Azure
            > Learn how Azure deployment works in Aspire.

            After authentication succeeds, `aspire deploy` still needs a small set of shared Azure settings. | Setting | Environment variable | Purpose | | ---------------------- | ----------------------- | ---------------------------------------------- | | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription | | `Azure:Location` | `Azure__Location` | Default Azure region for provisioned resources | ### Local settings [Section titled "Local settings"](#local-settings) Continue here.
            """;

        var service = CreateService(CreateMockFetcher(content));

        var document = await service.GetDocumentAsync("deploy-to-azure");
        Assert.NotNull(document);

        var normalized = document.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\n| Setting | Environment variable | Purpose |\n", normalized);
        Assert.Contains("\n| `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |\n", normalized);
        Assert.Contains("\n### Local settings\n", normalized);
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
    public async Task SearchAsync_TitleAndSlugPhraseMatch_OutranksRepeatedBodyAndCodeMatches()
    {
        var content = """
            # Service Discovery
            > Learn about service discovery in Aspire.

            Service discovery content.

            # Connect to Azure Service Bus
            > Connect to Azure Service Bus for messaging.

            ## Troubleshooting
            Service discovery is mentioned in troubleshooting. Service discovery appears again.
            `ServiceDiscoveryOptions` `ServiceDiscoveryOptions` `ServiceDiscoveryOptions`
            `ServiceDiscoveryOptions` `ServiceDiscoveryOptions` `ServiceDiscoveryOptions`
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("service discovery");

        Assert.NotEmpty(results);
        Assert.Equal("Service Discovery", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_CommandTitleAndSlug_OutrankReleaseNotesForCommonTerms()
    {
        var content = """
            # aspire new command
            > Learn about the aspire new command and its usage.

            This command creates new Aspire projects or solutions.

            # What's new in Aspire 13.2
            > Aspire 13.2 is here with new CLI commands, TypeScript AppHost support, and new and improved integrations.

            ## CLI enhancements
            Aspire has new commands, new templates, new project creation, and new integration updates.
            New Aspire experiences are easier to use with new command-line workflows.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("aspire new");

        Assert.NotEmpty(results);
        Assert.Equal("aspire new command", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_TitlePhrase_OutranksRepeatedCommonAzureTerms()
    {
        var content = """
            # Configure Azure Container Apps environments
            > Learn how to configure Azure Container Apps environment settings, scaling rules, and ingress.

            Configure environment details.

            # Manage Azure role assignments
            > Learn how to manage Azure role assignments.

            Azure resources use Azure role assignments. Azure permissions can apply to Azure Container Apps.
            Azure Container Apps environment names can appear in Azure role assignment examples.
            Azure role assignment examples repeat Azure terms many times.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("azure container apps environment");

        Assert.NotEmpty(results);
        Assert.Equal("Configure Azure Container Apps environments", results[0].Title);
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
    public async Task SearchAsync_ReleaseNotesIdentityMatch_RequiresTokenBoundaryMatch()
    {
        var content = """
            # Set up Go apps
            > Configure applications.

            Basic setup.

            # Changelog
            > Complete changelog for Aspire.

            ## Go updates
            Go support was added. Go improvements. Go templates. Go resources.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("go");

        Assert.NotEmpty(results);
        Assert.Equal("Set up Go apps", results[0].Title);
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

    [Theory]
    [InlineData("whats new 13.4")]
    [InlineData("what's new 13.4")]
    [InlineData("what new 13.4")]
    [InlineData("13-4 whats new")]
    public async Task SearchAsync_WhatsNewQuery_RanksReleaseNotesAboveNoisyMatches(string query)
    {
        var content = """
            # Configure Azure Container Apps environments
            > Learn how to configure new Aspire 13.4 environment settings, scaling rules, and ingress.

            ## Use an existing ACA environment
            Aspire 13.4 includes new environment options. New projects can use new environment settings in 13.4.

            # Create custom hosting integrations
            > Learn how to create a new custom Aspire hosting integration for an existing containerized application.

            ## Add a .NET service project to the AppHost for testing
            New Aspire 13.4 hosting integration samples can test new service behavior with a new AppHost.

            # What's new in Aspire 13.2
            > Aspire 13.2 is here with new CLI commands, TypeScript AppHost support, and new and improved integrations.

            ## CLI enhancements
            New commands, new templates, and new integration updates make Aspire 13.2 easier to use.

            # What's new in Aspire 13.4
            > Explore Aspire 13.4: GA TypeScript AppHost, new Go, Blazor WebAssembly, and Bun hosting integrations, richer Kubernetes and AKS deployment APIs, AI agent readiness, process resource commands, CLI integration discovery, and dashboard updates.

            ## Upgrade to Aspire 13.4
            Aspire 13.4 includes breaking changes. Review the release notes before upgrading.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync(query);

        Assert.NotEmpty(results);
        Assert.Equal("What's new in Aspire 13.4", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_ReleaseNotesPenalty_StillAppliesToIncidentalFeatureMatches()
    {
        var content = """
            # JavaScript Integration
            > How to use JavaScript with Aspire.

            JavaScript integration details.

            # What's new in Aspire 13.4
            > Release notes for Aspire 13.4 with JavaScript updates.

            JavaScript support was added. JavaScript is now fully supported.
            JavaScript JavaScript JavaScript. We love JavaScript!
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync("javascript");

        Assert.NotEmpty(results);
        Assert.Equal("JavaScript Integration", results[0].Title);
    }

    [Theory]
    [InlineData("13.4")]
    [InlineData("13-4")]
    public async Task SearchAsync_VersionQuery_MatchesSlugWithCollapsedVersion(string query)
    {
        var content = """
            # Aspire 13.4 Release Notes
            > Release notes for Aspire.

            Upgrade details.

            # Other Documentation
            > Some other docs.

            Nothing about the release here.
            """;

        var fetcher = CreateMockFetcher(content);
        var service = CreateService(fetcher);

        var results = await service.SearchAsync(query);

        Assert.NotEmpty(results);
        Assert.Equal("Aspire 13.4 Release Notes", results[0].Title);
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

    private sealed class SequenceDocsFetcher(IEnumerable<string?> contents) : IDocsFetcher
    {
        private readonly Queue<string?> _contents = new(contents);

        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_contents.Count > 0 ? _contents.Dequeue() : null);
        }
    }

    private sealed class ThrowingDocsFetcher(Exception exception) : IDocsFetcher
    {
        public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    [Fact]
    public void NormalizeContent_WithEmptyString_ReturnsEmpty()
    {
        var result = DocsIndexService.NormalizeContent("");
        Assert.Equal("", result);
    }

    [Fact]
    public void NormalizeContent_WithWhitespaceOnly_ReturnsWhitespace()
    {
        var result = DocsIndexService.NormalizeContent("   ");
        Assert.Equal("   ", result);
    }

    [Fact]
    public void NormalizeContent_NormalizesLineEndings()
    {
        var result = DocsIndexService.NormalizeContent("line1\r\nline2\rline3\nline4");
        Assert.Equal("""
            line1
            line2
            line3
            line4
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_SplitsInlineHeadings()
    {
        var result = DocsIndexService.NormalizeContent("Some text ## Heading\nMore text");
        Assert.Equal("""
            Some text

            ## Heading

            More text
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_RemovesSectionTitledBookmarks()
    {
        var result = DocsIndexService.NormalizeContent("Before [Section titled Overview](#overview) After");
        Assert.Equal("""
            Before

            After
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_SplitsInlineOrderedLists()
    {
        var result = DocsIndexService.NormalizeContent("Some text 1. First item");
        Assert.Equal("""
            Some text
            1. First item
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_SplitsInlineUnorderedLists()
    {
        var result = DocsIndexService.NormalizeContent("Some text * List item");
        Assert.Equal("""
            Some text
            * List item
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_CollapsesExcessBlankLines()
    {
        var result = DocsIndexService.NormalizeContent("""
            Paragraph 1




            Paragraph 2
            """);
        Assert.Equal("""
            Paragraph 1

            Paragraph 2
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_StripsTrailingWhitespaceBeforeNewlines()
    {
        var result = DocsIndexService.NormalizeContent("line1   \nline2\t\nline3");
        Assert.Equal("""
            line1
            line2
            line3
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_StripsLeadingWhitespace()
    {
        var result = DocsIndexService.NormalizeContent("""
              indented line
                more indented
            """);
        Assert.Equal("""
            indented line
            more indented
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_PreservesCodeBlockContent()
    {
        var input = """
            Before
            ```csharp
              var x = 1;
              var y = 2;
            ```
            After
            """;
        var result = DocsIndexService.NormalizeContent(input);
        Assert.Equal("""
            Before

            ```csharp
              var x = 1;
              var y = 2;
            ```

            After
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_ExpandsInlineCodeBlocks()
    {
        var result = DocsIndexService.NormalizeContent("""Text ```csharp Console.WriteLine("hello");``` more""");
        Assert.Equal("""
            Text
            ```csharp
            Console.WriteLine("hello");
            ```
            more
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_SplitsInlineTables()
    {
        var result = DocsIndexService.NormalizeContent("Text |Col1|Col2|Col3| |---|---|---| |A|B|C|");
        Assert.Equal("""
            Text
            |Col1|Col2|Col3|
            |---|---|---|
            |A|B|C|
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_TrimsResult()
    {
        var result = DocsIndexService.NormalizeContent("\n\n  Hello world  \n\n");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void NormalizeContent_HandlesComplexDocument()
    {
        var input = """
            # Title
            Some intro text ## Configuration
            Use `aspire run`. 1. Step one 2. Step two



            ```bash
            aspire run
            ```
            Done.
            """;
        var result = DocsIndexService.NormalizeContent(input);

        Assert.Equal("""
            # Title

            Some intro text

            ## Configuration

            Use `aspire run`.
            1. Step one
            2. Step two

            ```bash
            aspire run
            ```

            Done.
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_AddsBlankLineAfterTable()
    {
        var result = DocsIndexService.NormalizeContent("Header text |Col1|Col2| |---|---| |A|B| Footer text");
        Assert.Equal("""
            Header text
            |Col1|Col2|
            |---|---|
            |A|B|

            Footer text
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_AddsBlankLineAfterList()
    {
        var result = DocsIndexService.NormalizeContent("Intro text 1. First 2. Second\nFollowing paragraph");
        Assert.Equal("""
            Intro text
            1. First
            2. Second

            Following paragraph
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_AddsBlankLineAfterUnorderedList()
    {
        var result = DocsIndexService.NormalizeContent("Intro text * Item one * Item two\nFollowing paragraph");
        Assert.Equal("""
            Intro text
            * Item one
            * Item two

            Following paragraph
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_AddsBlankLineOnlyAfterRootList()
    {
        var result = DocsIndexService.NormalizeContent("Intro 1. First * Sub A * Sub B 2. Second\nFollowing paragraph");
        Assert.Equal("""
            Intro
            1. First
            * Sub A
            * Sub B
            2. Second

            Following paragraph
            """, result, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void NormalizeContent_NoBlankLineAfterListBeforeCodeBlock()
    {
        var result = DocsIndexService.NormalizeContent("Intro * Bash ```bash echo hello``` * PowerShell ```powershell Write-Host hello``` Done.");
        Assert.Equal("""
            Intro
            * Bash
            ```bash
            echo hello
            ```
            * PowerShell
            ```powershell
            Write-Host hello
            ```
            Done.
            """, result, ignoreLineEndingDifferences: true);
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
        public Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryDocsCache : IDocsCache
    {
        private readonly Dictionary<string, string> _content = [];
        private readonly Dictionary<string, string?> _etags = [];
        private LlmsDocument[]? _index;
        private string? _indexSourceFingerprint;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _content.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            _content[key] = content;
            return Task.CompletedTask;
        }

        public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
        {
            _etags.TryGetValue(url, out var value);
            return Task.FromResult(value);
        }

        public Task SetETagAsync(string url, string? etag, CancellationToken cancellationToken = default)
        {
            if (etag is null)
            {
                _etags.Remove(url);
            }
            else
            {
                _etags[url] = etag;
            }

            return Task.CompletedTask;
        }

        public Task<LlmsDocument[]?> GetIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_index);

        public Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default)
        {
            _index = documents;
            return Task.CompletedTask;
        }

        public Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_indexSourceFingerprint);

        public Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            _indexSourceFingerprint = fingerprint;
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        {
            _content.Remove(key);
            return Task.CompletedTask;
        }
    }
}
