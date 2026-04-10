// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsFetcherTests
{
    private const string DefaultSitemapUrl = "https://aspire.dev/sitemap-0.xml";
    private const string DefaultPageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods";

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
