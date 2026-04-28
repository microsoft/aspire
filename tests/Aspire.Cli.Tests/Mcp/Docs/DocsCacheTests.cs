// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Documentation.Docs;

public class DocsCacheTests(ITestOutputHelper outputHelper)
{
    private const string DefaultLlmsTxtUrl = "https://aspire.dev/llms-small.txt";

    [Fact]
    public async Task FetchDocsAsync_PersistsFriendlyLlmsCacheFileName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        var cache = CreateCache(workspace, memoryCache);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Content")
        };

        using var handler = new MockHttpMessageHandler(response);
        using var httpClient = new HttpClient(handler);
        var fetcher = new DocsFetcher(httpClient, cache, configuration, NullLogger<DocsFetcher>.Instance);

        _ = await fetcher.FetchDocsAsync().DefaultTimeout();

        var cacheFiles = Directory.GetFiles(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "docs"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("llms-small.txt", cacheFiles);
        Assert.DoesNotContain("https___aspire.dev_llms-small.txt.txt", cacheFiles);
    }

    [Fact]
    public async Task GetIndexAsync_ClearsLegacyUrlBackedDocsFiles_WhenModernIndexExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = CreateCache(workspace, memoryCache);
        var contentCache = CreateContentCache(workspace, memoryCache);

        await cache.SetAsync(DefaultLlmsTxtUrl, "# Legacy Content").DefaultTimeout();
        await cache.SetETagAsync(DefaultLlmsTxtUrl, "\"legacy-etag\"").DefaultTimeout();
        var legacyIndexKey = DocsSourceConfiguration.GetLegacyIndexCacheKey(DefaultLlmsTxtUrl);
        await contentCache.SetJsonAsync(
            legacyIndexKey,
            [
                new LlmsDocument
                {
                    Title = "Legacy Document",
                    Slug = "legacy-document",
                    Content = "# Legacy Document",
                    Sections = [],
                    Summary = "Legacy summary"
                }
            ],
            Aspire.Cli.JsonSourceGenerationContext.Default.LlmsDocumentArray).DefaultTimeout();
        await contentCache.SetAsync($"{legacyIndexKey}:fingerprint", "legacy-fingerprint").DefaultTimeout();
        await cache.SetIndexAsync(
            [
                new LlmsDocument
                {
                    Title = "Document",
                    Slug = "document",
                    Content = "# Document",
                    Sections = [],
                    Summary = "Summary"
                }
            ]).DefaultTimeout();
        await cache.SetIndexSourceFingerprintAsync("current-fingerprint").DefaultTimeout();

        _ = await cache.GetIndexAsync().DefaultTimeout();
        _ = await cache.GetIndexSourceFingerprintAsync().DefaultTimeout();

        var cacheFiles = Directory.GetFiles(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache", "docs"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.DoesNotContain("https___aspire.dev_llms-small.txt.txt", cacheFiles);
        Assert.DoesNotContain("https___aspire.dev_llms-small.txt.etag.txt", cacheFiles);
        Assert.DoesNotContain("index_https___aspire.dev_llms-small.txt.json", cacheFiles);
        Assert.DoesNotContain("index_https___aspire.dev_llms-small.txt_fingerprint.txt", cacheFiles);
        Assert.Contains("index_llms-small.json", cacheFiles);
        Assert.Contains("index_llms-small_fingerprint.txt", cacheFiles);
    }

    private static DocsCache CreateCache(TemporaryWorkspace workspace, IMemoryCache memoryCache)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new DocsCache(memoryCache, CreateExecutionContext(workspace), configuration, NullLogger<DocsCache>.Instance);
    }

    private static Aspire.Cli.Documentation.FileBackedDocumentContentCache CreateContentCache(TemporaryWorkspace workspace, IMemoryCache memoryCache)
        => new(memoryCache, CreateExecutionContext(workspace), "docs", NullLogger<DocsCache>.Instance);

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace)
        => new(
            workspace.WorkspaceRoot,
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")),
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")),
            "test.log");
}
