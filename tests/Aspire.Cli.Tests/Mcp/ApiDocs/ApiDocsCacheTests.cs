// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsCacheTests(ITestOutputHelper outputHelper)
{
    private const string DefaultSitemapUrl = "https://aspire.dev/sitemap-0.xml";
    private const string DefaultPageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods";

    [Fact]
    public async Task FetchSitemapAsync_PersistsFriendlySitemapCacheFileNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        var cache = CreateCache(workspace, memoryCache, configuration);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<urlset></urlset>")
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var fetcher = new ApiDocsFetcher(httpClient, cache, configuration, NullLogger<ApiDocsFetcher>.Instance);

        _ = await fetcher.FetchSitemapAsync().DefaultTimeout();

        var cacheFiles = GetCacheFiles(workspace, "api-docs");
        Assert.Contains("sitemap-0.txt", cacheFiles);
        Assert.Contains("sitemap-0.etag.txt", cacheFiles);
        Assert.DoesNotContain(cacheFiles, static file => file.StartsWith("https___aspire.dev_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchPageAsync_PersistsFriendlyPageCacheFileNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        var cache = CreateCache(workspace, memoryCache, configuration);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Methods")
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"");

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var fetcher = new ApiDocsFetcher(httpClient, cache, configuration, NullLogger<ApiDocsFetcher>.Instance);

        _ = await fetcher.FetchPageAsync(DefaultPageUrl).DefaultTimeout();

        var cacheFiles = GetCacheFiles(workspace, "api-docs");
        Assert.Contains($"{ApiDocsSourceConfiguration.GetPageContentCacheKey(DefaultPageUrl, DefaultSitemapUrl)}.txt", cacheFiles);
        Assert.Contains($"{ApiDocsSourceConfiguration.GetPageContentCacheKey(DefaultPageUrl, DefaultSitemapUrl)}.etag.txt", cacheFiles);
        Assert.DoesNotContain(cacheFiles, static file => file.StartsWith("https___aspire.dev_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetIndexAsync_PersistsFriendlyIndexFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        var cache = CreateCache(workspace, memoryCache, configuration);

        ApiReferenceItem[] items =
        [
            new ApiReferenceItem
            {
                Id = "csharp/aspire.test.package",
                Name = "Aspire.Test.Package",
                Language = ApiReferenceLanguages.CSharp,
                Kind = ApiReferenceKinds.Package,
                PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/"
            }
        ];

        await cache.SetIndexAsync(items).DefaultTimeout();
        await cache.SetIndexSourceFingerprintAsync("fingerprint").DefaultTimeout();

        var cachedItems = await cache.GetIndexAsync().DefaultTimeout();
        var fingerprint = await cache.GetIndexSourceFingerprintAsync().DefaultTimeout();

        var item = Assert.Single(cachedItems!);
        Assert.Equal("csharp/aspire.test.package", item.Id);
        Assert.Equal("fingerprint", fingerprint);

        var cacheFiles = GetCacheFiles(workspace, "api-docs");
        Assert.Contains("index_sitemap-0.json", cacheFiles);
        Assert.Contains("index_sitemap-0_fingerprint.txt", cacheFiles);
    }

    private static ApiDocsCache CreateCache(TemporaryWorkspace workspace, IMemoryCache memoryCache, IConfiguration configuration)
        => new(memoryCache, CreateExecutionContext(workspace), configuration, NullLogger<ApiDocsCache>.Instance);

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace)
        => new(
            workspace.WorkspaceRoot,
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")),
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")),
            "test.log");

    private static string[] GetCacheFiles(TemporaryWorkspace workspace, string subdirectory)
        => Directory.GetFiles(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", subdirectory))
            .Select(Path.GetFileName)
            .ToArray()!;
}
