// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Documentation;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsFetcherTests
{
    private const string DefaultSitemapUrl = "https://aspire.dev/sitemap-index.xml";
    private const string DefaultPageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods";
    private const string TruncatedUrlSet = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package</loc>""";

    private static ApiDocsFetcher CreateFetcher(HttpClient httpClient, IApiDocsCache cache, IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        return new ApiDocsFetcher(httpClient, cache, configuration, NullLogger<ApiDocsFetcher>.Instance);
    }

    [Fact]
    public async Task FetchSitemapAsync_CachesContentWithFriendlyKey()
    {
        const string expectedContent = "<urlset></urlset>";

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent)
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

        using var handler = new MockHttpMessageHandler(response, request =>
        {
            Assert.Equal(DefaultSitemapUrl, request.RequestUri?.ToString());
        });
        using var httpClient = new HttpClient(handler);
        var cache = new MockApiDocsCache();
        var fetcher = CreateFetcher(httpClient, cache);

        _ = await fetcher.FetchSitemapAsync();

        var cacheKey = ApiDocsSourceConfiguration.GetSitemapContentCacheKey(DefaultSitemapUrl);
        Assert.Equal(expectedContent, await cache.GetAsync(cacheKey));
        Assert.Equal("\"etag\"", await cache.GetETagAsync(cacheKey));
    }

    [Fact]
    public async Task FetchPageAsync_CachesContentWithFriendlyKey()
    {
        const string expectedContent = "# Methods";

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent)
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

        using var handler = new MockHttpMessageHandler(response, request =>
        {
            Assert.Equal($"{DefaultPageUrl}.md", request.RequestUri?.ToString());
        });
        using var httpClient = new HttpClient(handler);
        var cache = new MockApiDocsCache();
        var fetcher = CreateFetcher(httpClient, cache);

        _ = await fetcher.FetchPageAsync(DefaultPageUrl);

        var cacheKey = ApiDocsSourceConfiguration.GetPageContentCacheKey(DefaultPageUrl, DefaultSitemapUrl);
        Assert.Equal(expectedContent, await cache.GetAsync(cacheKey));
        Assert.Equal("\"etag\"", await cache.GetETagAsync(cacheKey));
    }

    [Fact]
    public async Task FetchPageAsync_StripsMemberAnchorFromMarkdownFetchAndCacheKey()
    {
        const string expectedContent = "# Methods";
        const string anchoredPageUrl = $"{DefaultPageUrl}#withcommand-iresourcebuilder-t-string";

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(expectedContent)
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

        using var handler = new MockHttpMessageHandler(response, request =>
        {
            Assert.Equal($"{DefaultPageUrl}.md", request.RequestUri?.ToString());
        });
        using var httpClient = new HttpClient(handler);
        var cache = new MockApiDocsCache();
        var fetcher = CreateFetcher(httpClient, cache);

        _ = await fetcher.FetchPageAsync(anchoredPageUrl);

        var cacheKey = ApiDocsSourceConfiguration.GetPageContentCacheKey(anchoredPageUrl, DefaultSitemapUrl);
        Assert.Equal(expectedContent, await cache.GetAsync(cacheKey));
        Assert.Equal("\"etag\"", await cache.GetETagAsync(cacheKey));
    }

    [Fact]
    public async Task FetchSitemapAsync_MergesChildSitemapsFromSitemapIndex()
    {
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml"),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package", "https://aspire.dev/docs/get-started"),
            ["https://aspire.dev/sitemap-api.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package", "https://aspire.dev/reference/api/typescript/aspire/builder")
        };
        var requests = new List<string>();
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, requests));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache());

        var merged = await fetcher.FetchSitemapAsync();

        Assert.NotNull(merged);
        Assert.Equal(
            [
                "https://aspire.dev/reference/api/csharp/aspire.test.package",
                "https://aspire.dev/reference/api/typescript/aspire/builder"
            ],
            ApiSitemapParser.Parse(merged).Select(static entry => entry.Url));
        Assert.Equal(
            new[] { DefaultSitemapUrl, "https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml" }.Order(StringComparer.Ordinal),
            requests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FetchSitemapAsync_ReturnsUrlSetContentUnchanged()
    {
        var content = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package");
        var sitemaps = new Dictionary<string, string> { ["http://localhost:4321/sitemap-0.xml"] = content };
        var requests = new List<string>();
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, requests));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache(), CreateConfiguration("http://localhost:4321/sitemap-0.xml"));

        var result = await fetcher.FetchSitemapAsync();

        Assert.Equal(content, result);
        Assert.Equal(["http://localhost:4321/sitemap-0.xml"], requests);
    }

    [Fact]
    public async Task FetchSitemapAsync_RebasesChildSitemapsOntoConfiguredHost()
    {
        var sitemaps = new Dictionary<string, string>
        {
            ["http://localhost:4321/sitemap-index.xml"] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml"),
            ["http://localhost:4321/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started"),
            ["http://localhost:4321/sitemap-api.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package")
        };
        var requests = new List<string>();
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, requests));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache(), CreateConfiguration("http://localhost:4321/sitemap-index.xml"));

        var merged = await fetcher.FetchSitemapAsync();

        Assert.NotNull(merged);
        Assert.All(requests, static request => Assert.StartsWith("http://localhost:4321/", request, StringComparison.Ordinal));
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task FetchSitemapAsync_UsesIndependentETagForEachChildSitemap()
    {
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml"),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started"),
            ["https://aspire.dev/sitemap-api.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package")
        };
        var requests = new List<string>();
        var notModified = new List<string>();
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, requests, notModified));
        var cache = new MockApiDocsCache();
        var fetcher = CreateFetcher(httpClient, cache);

        var first = await fetcher.FetchSitemapAsync();

        sitemaps["https://aspire.dev/sitemap-api.xml"] = UrlSet(
            "https://aspire.dev/reference/api/csharp/aspire.test.package",
            "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype");
        var second = await fetcher.FetchSitemapAsync();

        Assert.Equal(
            ["https://aspire.dev/sitemap-0.xml", DefaultSitemapUrl],
            notModified.Order(StringComparer.Ordinal));
        Assert.NotEqual(
            SourceContentFingerprint.Compute(first!, schemaVersion: 1),
            SourceContentFingerprint.Compute(second!, schemaVersion: 1));
    }

    [Fact]
    public async Task FetchSitemapAsync_ProducesSameContentRegardlessOfChildOrder()
    {
        var sitemaps = new Dictionary<string, string>
        {
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/a"),
            ["https://aspire.dev/sitemap-api.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/b")
        };

        sitemaps[DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml");
        using var firstClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var first = await CreateFetcher(firstClient, new MockApiDocsCache()).FetchSitemapAsync();

        sitemaps[DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-api.xml", "https://aspire.dev/sitemap-0.xml");
        using var secondClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var second = await CreateFetcher(secondClient, new MockApiDocsCache()).FetchSitemapAsync();

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task FetchSitemapAsync_ReturnsNullWhenChildSitemapFailsWithoutCache()
    {
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml"),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started")
        };
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache());

        var result = await fetcher.FetchSitemapAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchSitemapAsync_UsesCachedChildSitemapWhenRefreshFails()
    {
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", "https://aspire.dev/sitemap-api.xml"),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started"),
            ["https://aspire.dev/sitemap-api.xml"] = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package")
        };
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var cache = new MockApiDocsCache();
        var fetcher = CreateFetcher(httpClient, cache);
        var first = await fetcher.FetchSitemapAsync();

        sitemaps.Remove("https://aspire.dev/sitemap-api.xml");
        var second = await fetcher.FetchSitemapAsync();

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task FetchSitemapAsync_ReturnsNullWhenSitemapIndexHasNoChildren()
    {
        var sitemaps = new Dictionary<string, string> { [DefaultSitemapUrl] = SitemapIndex() };
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache());

        var result = await fetcher.FetchSitemapAsync();

        Assert.Null(result);
    }

    [Theory]
    [InlineData(TruncatedUrlSet)]
    [InlineData("<!DOCTYPE html><html><body>Bad gateway</body></html>")]
    [InlineData("Bad gateway")]
    public async Task FetchSitemapAsync_ReturnsNullWhenChildSitemapIsNotWellFormedXml(string childContent)
    {
        const string childUrl = "https://aspire.dev/sitemap-api.xml";
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", childUrl),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started"),
            [childUrl] = childContent
        };
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var logger = new FakeLogger<ApiDocsFetcher>();
        var fetcher = new ApiDocsFetcher(httpClient, new MockApiDocsCache(), new ConfigurationBuilder().Build(), logger);

        var result = await fetcher.FetchSitemapAsync();

        Assert.Null(result);
        var warning = Assert.Single(logger.Collector.GetSnapshot(), static record => record.Level is LogLevel.Warning);
        Assert.Contains(childUrl, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchSitemapAsync_RecoversWhenMalformedChildSitemapIsFixed()
    {
        const string childUrl = "https://aspire.dev/sitemap-api.xml";
        var goodChild = UrlSet("https://aspire.dev/reference/api/csharp/aspire.test.package");
        var sitemaps = new Dictionary<string, string>
        {
            [DefaultSitemapUrl] = SitemapIndex("https://aspire.dev/sitemap-0.xml", childUrl),
            ["https://aspire.dev/sitemap-0.xml"] = UrlSet("https://aspire.dev/docs/get-started"),
            [childUrl] = goodChild
        };
        using var httpClient = new HttpClient(CreateSitemapHandler(sitemaps, []));
        var fetcher = CreateFetcher(httpClient, new MockApiDocsCache());
        var first = await fetcher.FetchSitemapAsync();

        sitemaps[childUrl] = TruncatedUrlSet;
        var second = await fetcher.FetchSitemapAsync();

        sitemaps[childUrl] = goodChild;
        var third = await fetcher.FetchSitemapAsync();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(first, third);
    }

    private static IConfiguration CreateConfiguration(string sitemapUrl)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigPath] = sitemapUrl
            })
            .Build();

    private static string SitemapIndex(params string[] childUrls)
        => $"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(childUrls.Select(static url => $"<sitemap><loc>{url}</loc></sitemap>"))}</sitemapindex>""";

    private static string UrlSet(params string[] pageUrls)
        => $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(pageUrls.Select(static url => $"<url><loc>{url}</loc></url>"))}</urlset>""";

    // Serves sitemaps by URL with an ETag derived from the content. Requests carrying a matching
    // If-None-Match are answered with 304, and URLs without a configured sitemap fail with 404.
    private static MockHttpMessageHandler CreateSitemapHandler(Dictionary<string, string> sitemaps, List<string> requests, List<string>? notModified = null)
        => new(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);

            if (!sitemaps.TryGetValue(url, out var content))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))}\"";
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == etag))
            {
                notModified?.Add(url);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return response;
        });

    private sealed class MockApiDocsCache : IApiDocsCache
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _etags = new(StringComparer.OrdinalIgnoreCase);
        private ApiReferenceItem[]? _index;
        private string? _indexFingerprint;
        private ApiReferenceItem[]? _memberIndex;
        private string? _memberIndexFingerprint;
        private string[]? _indexedMemberContainerIds;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_content.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            _content[key] = content;
            return Task.CompletedTask;
        }

        public Task<string?> GetETagAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(_etags.TryGetValue(url, out var value) ? value : null);

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

        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        {
            _content.Remove(key);
            return Task.CompletedTask;
        }

        public Task<ApiReferenceItem[]?> GetIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_index);

        public Task SetIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        {
            _index = documents;
            return Task.CompletedTask;
        }

        public Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_indexFingerprint);

        public Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            _indexFingerprint = fingerprint;
            return Task.CompletedTask;
        }

        public Task<ApiReferenceItem[]?> GetMemberIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_memberIndex);

        public Task SetMemberIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        {
            _memberIndex = documents;
            return Task.CompletedTask;
        }

        public Task<string?> GetMemberIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_memberIndexFingerprint);

        public Task SetMemberIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            _memberIndexFingerprint = fingerprint;
            return Task.CompletedTask;
        }

        public Task<string[]?> GetIndexedMemberContainerIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_indexedMemberContainerIds);

        public Task SetIndexedMemberContainerIdsAsync(string[] containerIds, CancellationToken cancellationToken = default)
        {
            _indexedMemberContainerIds = containerIds;
            return Task.CompletedTask;
        }
    }
}
