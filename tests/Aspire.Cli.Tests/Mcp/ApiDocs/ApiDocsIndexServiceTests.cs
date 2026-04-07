// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsIndexServiceTests
{
    private const string CSharpMethodsUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods";

    [Fact]
    public async Task ListAsync_BuildsHierarchyForBothLanguages()
    {
        var service = CreateService();

        var csharpRoots = await service.ListAsync("csharp");
        var typeScriptRoots = await service.ListAsync("typescript");
        var csharpChildren = await service.ListAsync("csharp/aspire.test.package");
        var csharpTypeChildren = await service.ListAsync("csharp/aspire.test.package/testtype");
        var typeScriptChildren = await service.ListAsync("typescript/aspire.hosting.test");

        Assert.Collection(
            csharpRoots,
            item =>
            {
                Assert.Equal("csharp/aspire.test.package", item.Id);
                Assert.Equal(ApiReferenceKinds.Package, item.Kind);
            });

        Assert.Collection(
            typeScriptRoots,
            item =>
            {
                Assert.Equal("typescript/aspire.hosting.test", item.Id);
                Assert.Equal(ApiReferenceKinds.Module, item.Kind);
            });

        Assert.Collection(
            csharpChildren,
            item =>
            {
                Assert.Equal("csharp/aspire.test.package/testtype", item.Id);
                Assert.Equal(ApiReferenceKinds.Type, item.Kind);
            });

        Assert.Collection(
            csharpTypeChildren,
            item =>
            {
                Assert.Equal("csharp/aspire.test.package/testtype/methods", item.Id);
                Assert.Equal(ApiReferenceKinds.MemberGroup, item.Kind);
            });

        Assert.Collection(
            typeScriptChildren,
            item =>
            {
                Assert.Equal("typescript/aspire.hosting.test/testresource", item.Id);
                Assert.Equal(ApiReferenceKinds.Symbol, item.Kind);
            });
    }

    [Fact]
    public async Task ListAsync_BuildsGenericHierarchyForModeledFutureLanguages()
    {
        var service = CreateService();

        var pythonRoots = await service.ListAsync("python");
        var pythonChildren = await service.ListAsync("python/aspire.python.test");

        Assert.Collection(
            pythonRoots,
            item =>
            {
                Assert.Equal("python/aspire.python.test", item.Id);
                Assert.Equal(ApiReferenceKinds.Module, item.Kind);
            });

        Assert.Collection(
            pythonChildren,
            item =>
            {
                Assert.Equal("python/aspire.python.test/testresource", item.Id);
                Assert.Equal(ApiReferenceKinds.Symbol, item.Kind);
            });
    }

    [Fact]
    public async Task SearchAsync_RespectsLanguageFilterAndFindsDirectRouteItems()
    {
        var service = CreateService();

        var csharpResults = await service.SearchAsync("methods", ApiReferenceLanguages.CSharp, 10);
        var typeScriptResults = await service.SearchAsync("methods", ApiReferenceLanguages.TypeScript, 10);

        var result = Assert.Single(csharpResults);
        Assert.Equal("csharp/aspire.test.package/testtype/methods", result.Id);
        Assert.Equal(ApiReferenceKinds.MemberGroup, result.Kind);
        Assert.Empty(typeScriptResults);
    }

    [Fact]
    public async Task SearchAsync_FindsGenericItemsForModeledFutureLanguages()
    {
        var service = CreateService();

        var results = await service.SearchAsync("runemulator", ApiReferenceLanguages.Python, 10);

        var result = Assert.Single(results);
        Assert.Equal("python/aspire.python.test/testresource/runemulator", result.Id);
        Assert.Equal(ApiReferenceKinds.Member, result.Kind);
    }

    [Fact]
    public async Task GetAsync_ForDirectRouteItem_ReturnsRawMarkdown()
    {
        var service = CreateService();

        var item = await service.GetAsync("csharp/aspire.test.package/testtype/methods");

        Assert.NotNull(item);
        Assert.Equal("methods", item.Name);
        Assert.Equal(ApiReferenceKinds.MemberGroup, item.Kind);
        Assert.Contains("## DoThing(string) {#dothing-string}", item.Content, StringComparison.Ordinal);
        Assert.Contains("## DoOther() {#doother}", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RebasesConfiguredHostForFetchedContentAndReturnedUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigKey] = "http://localhost:4321/sitemap-0.xml"
            })
            .Build();

        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md"] = "# Methods\n\n## LocalOnly()"
            });

        var service = new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration, NullLogger<ApiDocsIndexService>.Instance);

        var item = await service.GetAsync("csharp/aspire.test.package/testtype/methods");

        Assert.NotNull(item);
        Assert.Equal("http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods", item.Url);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods"], fetcher.RequestedPageUrls);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md"], fetcher.RequestedMarkdownUrls);
        Assert.Contains("## LocalOnly()", item.Content, StringComparison.Ordinal);
    }

    private static ApiDocsIndexService CreateService(IConfiguration? configuration = null)
    {
        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.test/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.test/testresource/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.test/testresource/runasemulator/</loc></url>
              <url><loc>https://aspire.dev/reference/api/python/aspire.python.test/</loc></url>
              <url><loc>https://aspire.dev/reference/api/python/aspire.python.test/testresource/</loc></url>
              <url><loc>https://aspire.dev/reference/api/python/aspire.python.test/testresource/runemulator/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CSharpMethodsUrl] = """
                    # Methods

                    ## DoThing(string) {#dothing-string}

                    Does the thing.

                    ## DoOther() {#doother}

                    Does something else.
                    """,
            });

        return new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration ?? new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
    }

    private sealed class TestApiDocsFetcher(string sitemapContent, IReadOnlyDictionary<string, string> pageContent) : IApiDocsFetcher
    {
        public List<string> RequestedPageUrls { get; } = [];

        public List<string> RequestedMarkdownUrls { get; } = [];

        public Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(sitemapContent);

        public Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            RequestedPageUrls.Add(pageUrl);
            var markdownUrl = pageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? pageUrl : $"{pageUrl}.md";
            RequestedMarkdownUrls.Add(markdownUrl);
            return Task.FromResult(pageContent.TryGetValue(pageUrl, out var content) ? content : pageContent.TryGetValue(markdownUrl, out content) ? content : null);
        }
    }

    private sealed class TestApiDocsCache : IApiDocsCache
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _etags = new(StringComparer.OrdinalIgnoreCase);

        public ApiReferenceItem[]? Index { get; private set; }

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
            => Task.FromResult(Index);

        public Task SetIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        {
            Index = documents;
            return Task.CompletedTask;
        }
    }
}
