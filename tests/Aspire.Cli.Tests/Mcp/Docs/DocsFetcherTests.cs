// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Mcp.Docs;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp.Docs;

public class DocsFetcherTests
{
    [Fact]
    public async Task FetchDocsAsync_SuccessfulRequest_ReturnsContent()
    {
        var expectedContent = """
            # Redis Integration
            > Connect to Redis.

            Content here.
            """;

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(expectedContent)
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(expectedContent, content);
    }

    [Fact]
    public async Task FetchDocsAsync_StoresETag_WhenProvided()
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("# Content")
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        await fetcher.FetchDocsAsync();

        var storedETag = await cache.GetETagAsync("https://aspire.dev/llms-small.txt");
        Assert.Equal("\"abc123\"", storedETag);
    }

    [Fact]
    public async Task FetchDocsAsync_CachesContent()
    {
        var content = "# Cached Content";
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        await fetcher.FetchDocsAsync();

        var cached = await cache.GetAsync("https://aspire.dev/llms-small.txt");
        Assert.Equal(content, cached);
    }

    [Fact]
    public async Task FetchDocsAsync_WithCachedETag_SendsIfNoneMatchHeader()
    {
        var cache = new MockDocsCache();
        await cache.SetETagAsync("https://aspire.dev/llms-small.txt", "\"cached-etag\"");
        await cache.SetAsync("https://aspire.dev/llms-small.txt", "# Cached");

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotModified
        };

        using var handler = new MockHttpMessageHandler(response, request =>
        {
            Assert.Contains("\"cached-etag\"", request.Headers.IfNoneMatch.ToString());
        });

        using var httpClient = new HttpClient(handler);
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        await fetcher.FetchDocsAsync();

        Assert.True(handler.RequestValidated);
    }

    [Fact]
    public async Task FetchDocsAsync_NotModifiedResponse_ReturnsCachedContent()
    {
        var cachedContent = "# Cached Content";
        var cache = new MockDocsCache();
        await cache.SetETagAsync("https://aspire.dev/llms-small.txt", "\"etag\"");
        await cache.SetAsync("https://aspire.dev/llms-small.txt", cachedContent);

        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotModified
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(cachedContent, content);
    }

    [Fact]
    public async Task FetchDocsAsync_NotModifiedButCacheEmpty_RetriesWithoutETag()
    {
        var freshContent = "# Fresh Content";
        var cache = new MockDocsCache();
        await cache.SetETagAsync("https://aspire.dev/llms-small.txt", "\"etag\"");
        // Cache content is empty - simulating cache cleared but ETag remains

        var callCount = 0;
        using var handler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                // First call returns NotModified
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotModified };
            }
            // Retry returns fresh content
            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(freshContent)
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-etag\"");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(freshContent, content);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task FetchDocsAsync_FailedRequest_ReturnsNull()
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.InternalServerError
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Null(content);
    }

    [Fact]
    public async Task FetchDocsAsync_NetworkError_ReturnsNull()
    {
        using var handler = new MockHttpMessageHandler(new HttpRequestException("Network error"));
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Null(content);
    }

    [Fact]
    public async Task FetchDocsAsync_Cancellation_ReturnsNull()
    {
        // Use a handler that properly checks the cancellation token
        using var handler = new CancellationCheckingHandler();
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The FetchDocsAsync swallows exceptions and returns null when there's no cached content
        var result = await fetcher.FetchDocsAsync(cts.Token);
        Assert.Null(result);
    }

    [Fact]
    public async Task FetchDocsAsync_EmptyResponse_ReturnsEmptyString()
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("")
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal("", content);
    }

    [Fact]
    public async Task FetchDocsAsync_WhitespaceOnlyResponse_ReturnsWhitespace()
    {
        var whitespace = "   \n\t\n   ";
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(whitespace)
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(whitespace, content);
    }

    [Fact]
    public async Task FetchDocsAsync_NullContent_ReturnsEmptyString()
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = null
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        // When Content is set to null, .NET replaces it with EmptyContent, so ReadAsStringAsync returns empty string
        Assert.Equal("", content);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task FetchDocsAsync_ErrorStatusCode_ReturnsNull(HttpStatusCode statusCode)
    {
        using var response = new HttpResponseMessage
        {
            StatusCode = statusCode
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Null(content);
    }

    public static TheoryData<Exception> ExceptionData => new()
    {
        new TaskCanceledException("Request timed out"),
        new OperationCanceledException("Operation was cancelled"),
        new InvalidOperationException("Invalid state")
    };

    [Theory]
    [MemberData(nameof(ExceptionData))]
    public async Task FetchDocsAsync_Exception_ReturnsNull(Exception exception)
    {
        using var handler = new MockHttpMessageHandler(exception);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Null(content);
    }

    [Fact]
    public async Task FetchDocsAsync_CacheReturnsNull_FetchesFromServer()
    {
        var serverContent = "# Server Content";
        using var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(serverContent)
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var cache = new MockDocsCache();
        // Cache is empty, no ETag
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(serverContent, content);
    }

    [Fact]
    public async Task FetchDocsAsync_WithETagButNoCachedContent_ClearsETagAndRetries()
    {
        var serverContent = "# Fresh Content";
        var cache = new MockDocsCache();
        await cache.SetETagAsync("https://aspire.dev/llms-small.txt", "\"old-etag\"");
        // Note: no cached content set

        var callCount = 0;
        using var handler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage { StatusCode = HttpStatusCode.NotModified };
            }
            return new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(serverContent)
            };
        });

        using var httpClient = new HttpClient(handler);
        var fetcher = new DocsFetcher(httpClient, cache, NullLogger<DocsFetcher>.Instance);

        var content = await fetcher.FetchDocsAsync();

        Assert.Equal(serverContent, content);
        Assert.Equal(2, callCount);
    }

    private sealed class CancellationCheckingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("# Content")
            });
        }
    }

    private sealed class MockDocsCache : IDocsCache
    {
        private readonly Dictionary<string, string> _content = [];
        private readonly Dictionary<string, string> _etags = [];
        private LlmsDocument[]? _index;

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
        {
            return Task.FromResult(_index);
        }

        public Task SetIndexAsync(LlmsDocument[] documents, CancellationToken cancellationToken = default)
        {
            _index = documents;
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        {
            _content.Remove(key);
            return Task.CompletedTask;
        }
    }
}
