// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsIndexServiceTests
{
    private const string CSharpMethodsUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods";
    private const string CSharpTypeUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype";
    private const string CSharpWithEnvironmentMethodsUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods";
    private const string CSharpWithEnvironmentTypeUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions";

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
    public async Task SearchAsync_FindsMembersParsedFromGroupedMemberLinks()
    {
        var service = CreateService();

        var results = await service.SearchAsync("DoThing", ApiReferenceLanguages.CSharp, 10);

        var result = Assert.Single(results);
        Assert.Equal("csharp/aspire.test.package/testtype/methods#dothing", result.Id);
        Assert.Equal("DoThing(string)", result.Name);
        Assert.Equal(ApiReferenceKinds.Member, result.Kind);
        Assert.Equal("Does the thing.", result.Summary);
    }

    [Fact]
    public async Task SearchAsync_LoadsMemberIndexWhenBaseRouteHitsExist()
    {
        var service = CreateWithEnvironmentService();

        var results = await service.SearchAsync("WithEnv", ApiReferenceLanguages.CSharp, 10);

        Assert.Equal(3, results.Count);
        Assert.Contains(results, result => result.Id == "csharp/aspire.test.package/iresourcewithenvironment");
        Assert.Contains(results, result => result.Name == "WithEnvironment(IResourceBuilder<T>, string, string?)" &&
            result.Id.StartsWith("csharp/aspire.test.package/resourcebuilderextensions/methods#withenvironment-", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Name == "WithEnvironment(IResourceBuilder<T>, Action<EnvironmentCallbackContext>)" &&
            result.Id.StartsWith("csharp/aspire.test.package/resourcebuilderextensions/methods#withenvironment-", StringComparison.Ordinal));
        Assert.DoesNotContain(results, result => result.Id.Contains("iresourcebuilder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_PrefersTypesForBroadIdentifierQueries()
    {
        var service = CreateDistributedApplicationService();

        var results = await service.SearchAsync("distributedapp", ApiReferenceLanguages.CSharp, 10);

        Assert.NotEmpty(results);
        var firstResult = results[0];
        Assert.Equal("csharp/aspire.hosting/distributedapplication", firstResult.Id);
        Assert.Equal(ApiReferenceKinds.Type, firstResult.Kind);
        Assert.Contains(results, result => result.Id == "csharp/aspire.hosting.testing/distributedapplicationfactory");
        Assert.Contains(results, result => result.Kind == ApiReferenceKinds.Member);
    }

    [Fact]
    public async Task SearchAsync_LoadsOnlyNeededMemberContainers()
    {
        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/alphatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/alphatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/betatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/betatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/deltatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/deltatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/epsilontype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/epsilontype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/etatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/etatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/gammatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/gammatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/iotatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/iotatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/kappatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/kappatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/thetatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/thetatype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/zetatype/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/zetatype/methods/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://aspire.dev/reference/api/csharp/aspire.test.package/alphatype"] = """
                    # AlphaType

                    ## Methods

                    - [FindMe()](/reference/api/csharp/aspire.test.package/alphatype/methods.md#findme) : `void` -- Finds the result immediately.
                    """,
                ["https://aspire.dev/reference/api/csharp/aspire.test.package/betatype"] = """
                    # BetaType

                    ## Methods

                    - [DifferentThing()](/reference/api/csharp/aspire.test.package/betatype/methods.md#differentthing) : `void` -- Does something else.
                    """
            });

        var service = new ApiDocsIndexService(fetcher, new TestApiDocsCache(), new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);

        var results = await service.SearchAsync("FindMe", ApiReferenceLanguages.CSharp, 1);

        var result = Assert.Single(results);
        Assert.Equal("csharp/aspire.test.package/alphatype/methods#findme", result.Id);
        Assert.Contains("https://aspire.dev/reference/api/csharp/aspire.test.package/alphatype", fetcher.RequestedPageUrls);
        Assert.DoesNotContain("https://aspire.dev/reference/api/csharp/aspire.test.package/thetatype", fetcher.RequestedPageUrls);
        Assert.DoesNotContain("https://aspire.dev/reference/api/csharp/aspire.test.package/zetatype", fetcher.RequestedPageUrls);
    }

    [Fact]
    public async Task ListAsync_ForMemberGroupScope_ReturnsParsedMembers()
    {
        var service = CreateService();

        var items = await service.ListAsync("csharp/aspire.test.package/testtype/methods");

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("csharp/aspire.test.package/testtype/methods#doother", item.Id);
                Assert.Equal("DoOther()", item.Name);
                Assert.Equal(ApiReferenceKinds.Member, item.Kind);
                Assert.Equal("csharp/aspire.test.package/testtype/methods", item.ParentId);
            },
            item =>
            {
                Assert.Equal("csharp/aspire.test.package/testtype/methods#dothing", item.Id);
                Assert.Equal("DoThing(string)", item.Name);
                Assert.Equal(ApiReferenceKinds.Member, item.Kind);
                Assert.Equal("csharp/aspire.test.package/testtype/methods", item.ParentId);
            });
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
    public async Task GetAsync_ForParsedMember_ReturnsAnchoredUrlAndRawMarkdown()
    {
        var service = CreateService();

        var item = await service.GetAsync("csharp/aspire.test.package/testtype/methods#dothing");

        Assert.NotNull(item);
        Assert.Equal("DoThing(string)", item.Name);
        Assert.Equal(ApiReferenceKinds.Member, item.Kind);
        Assert.Equal($"{CSharpMethodsUrl}#dothing-string", item.Url);
        Assert.Contains("## DoThing(string) {#dothing-string}", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RebasesConfiguredHostForFetchedContentAndReturnedUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigPath] = "http://localhost:4321/sitemap-0.xml"
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
                ["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md"] = """
                    # Methods

                    - [Package](/reference/api/csharp/aspire.test.package.md)
                    - [CreateBuilder(string[])](/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)
                    - [Current](#localonly)

                    ## LocalOnly() {#localonly}
                    """
            });

        var service = new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration, NullLogger<ApiDocsIndexService>.Instance);

        var item = await service.GetAsync("csharp/aspire.test.package/testtype/methods");

        Assert.NotNull(item);
        Assert.Equal("http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods", item.Url);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods"], fetcher.RequestedPageUrls);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md"], fetcher.RequestedMarkdownUrls);
        Assert.Contains("[Package](http://localhost:4321/reference/api/csharp/aspire.test.package.md)", item.Content, StringComparison.Ordinal);
        Assert.Contains("[CreateBuilder(string[])](http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)", item.Content, StringComparison.Ordinal);
        Assert.Contains("[Current](http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods#localonly)", item.Content, StringComparison.Ordinal);
        Assert.Contains("## LocalOnly()", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RewritesBracketedMemberSignatureLinksFromDistributedApplicationPage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigPath] = "http://localhost:4321/sitemap-0.xml"
            })
            .Build();

        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication.md"] = """
                    # DistributedApplication

                    ## Methods

                    - [CreateBuilder(string[])](/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string) : [IDistributedApplicationBuilder](/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md) `static` `ats export` -- Creates a new instance of [IDistributedApplicationBuilder](/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md) with the specified command-line arguments.
                    """
            });

        var service = new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration, NullLogger<ApiDocsIndexService>.Instance);

        var item = await service.GetAsync("csharp/aspire.hosting/distributedapplication/");

        Assert.NotNull(item);
        Assert.Equal("http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication", item.Url);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication"], fetcher.RequestedPageUrls);
        Assert.Equal(["http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication.md"], fetcher.RequestedMarkdownUrls);
        Assert.Contains("[CreateBuilder(string[])](http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)", item.Content, StringComparison.Ordinal);
        Assert.Contains("[IDistributedApplicationBuilder](http://localhost:4321/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md)", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ForTypeScriptDirectRouteItem_ReturnsMarkdownFromConfiguredHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigPath] = "http://localhost:4321/sitemap-0.xml"
            })
            .Build();

        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.postgresql/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.postgresql/addpostgres/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http://localhost:4321/reference/api/typescript/aspire.hosting.postgresql/addpostgres.md"] = "# addPostgres\n\n## Parameters\n\n- name"
            });

        var service = new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration, NullLogger<ApiDocsIndexService>.Instance);

        var item = await service.GetAsync("typescript/aspire.hosting.postgresql/addpostgres");

        Assert.NotNull(item);
        Assert.Equal(ApiReferenceLanguages.TypeScript, item.Language);
        Assert.Equal(ApiReferenceKinds.Member, item.Kind);
        Assert.Equal("http://localhost:4321/reference/api/typescript/aspire.hosting.postgresql/addpostgres", item.Url);
        Assert.Equal(["http://localhost:4321/reference/api/typescript/aspire.hosting.postgresql/addpostgres"], fetcher.RequestedPageUrls);
        Assert.Equal(["http://localhost:4321/reference/api/typescript/aspire.hosting.postgresql/addpostgres.md"], fetcher.RequestedMarkdownUrls);
        Assert.Contains("# addPostgres", item.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureIndexedAsync_RebuildsCachedIndexWhenSitemapChangesAcrossInstances()
    {
        var cache = new TestApiDocsCache();
        var fetcher = new SequenceApiDocsFetcher(
        [
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.first.package/</loc></url>
            </urlset>
            """,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.second.package/</loc></url>
            </urlset>
            """
        ]);

        var firstService = new ApiDocsIndexService(fetcher, cache, new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
        var firstItems = await firstService.ListAsync("csharp");

        Assert.Collection(
            firstItems,
            item => Assert.Equal("csharp/aspire.first.package", item.Id));

        var secondService = new ApiDocsIndexService(fetcher, cache, new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
        var secondItems = await secondService.ListAsync("csharp");

        Assert.Collection(
            secondItems,
            item => Assert.Equal("csharp/aspire.second.package", item.Id));
    }

    [Fact]
    public async Task EnsureIndexedAsync_UsesCachedIndexWhenSitemapUnavailableAfterInitialLoad()
    {
        var cache = new TestApiDocsCache();
        var firstService = new ApiDocsIndexService(
            new SequenceApiDocsFetcher(
            [
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                  <url><loc>https://aspire.dev/reference/api/csharp/aspire.cached.package/</loc></url>
                </urlset>
                """
            ]),
            cache,
            new ConfigurationBuilder().Build(),
            NullLogger<ApiDocsIndexService>.Instance);

        await firstService.EnsureIndexedAsync();

        var secondService = new ApiDocsIndexService(
            new SequenceApiDocsFetcher([null]),
            cache,
            new ConfigurationBuilder().Build(),
            NullLogger<ApiDocsIndexService>.Instance);

        var items = await secondService.ListAsync("csharp");

        Assert.Collection(
            items,
            item => Assert.Equal("csharp/aspire.cached.package", item.Id));
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
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CSharpMethodsUrl] = """
                    # Methods
                    
                    ## DoThing(string) {#dothing-string}

                    - Name: `DoThing(string)`
                    - Returns: `void`

                    Does the thing.

                    ## Parameters

                    - `value` (`string`)
                      The value to process.

                    ## DoOther() {#doother}

                    - Name: `DoOther()`
                    - Returns: `void`

                    Does something else.
                    """,
                [CSharpTypeUrl] = """
                    # TestType

                    Provides a test type.

                    ## Methods

                    - [DoThing(string)](/reference/api/csharp/aspire.test.package/testtype/methods.md#dothing-string) : `void` `extension` -- Does the thing.
                    - [DoOther()](/reference/api/csharp/aspire.test.package/testtype/methods.md#doother) : `void` `extension` -- Does something else.
                    """,
            });

        return new ApiDocsIndexService(fetcher, new TestApiDocsCache(), configuration ?? new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
    }

    private static ApiDocsIndexService CreateWithEnvironmentService()
    {
        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/iresourcewithenvironment/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://aspire.dev/reference/api/csharp/aspire.test.package/iresourcewithenvironment"] = """
                    # IResourceWithEnvironment

                    Provides access to environment configuration.
                    """,
                [CSharpWithEnvironmentTypeUrl] = """
                    # ResourceBuilderExtensions

                    Provides extension methods for configuring resources with environment variables.

                    ## Methods

                    - [WithEnvironment(IResourceBuilder<T>, string, string?)](/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods.md#withenvironment-iresourcebuilder-t-string-string) : `IResourceBuilder<T>` `extension` -- Adds a string environment variable.
                    - [WithEnvironment(IResourceBuilder<T>, Action<EnvironmentCallbackContext>)](/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods.md#withenvironment-iresourcebuilder-t-action-environmentcallbackcontext) : `IResourceBuilder<T>` `extension` -- Adds environment variables from a callback.
                    """,
                [CSharpWithEnvironmentMethodsUrl] = """
                    # Methods

                    ## WithEnvironment(IResourceBuilder<T>, string, string?) {#withenvironment-iresourcebuilder-t-string-string}

                    Adds a string environment variable.

                    ## WithEnvironment(IResourceBuilder<T>, Action<EnvironmentCallbackContext>) {#withenvironment-iresourcebuilder-t-action-environmentcallbackcontext}

                    Adds environment variables from a callback.
                    """
            });

        return new ApiDocsIndexService(fetcher, new TestApiDocsCache(), new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
    }

    private static ApiDocsIndexService CreateDistributedApplicationService()
    {
        var fetcher = new TestApiDocsFetcher(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplicationbuilder/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplicationbuilder/constructors/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/idistributedapplicationeventingsubscriber/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/idistributedapplicationeventingsubscriber/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting.testing/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting.testing/distributedapplicationfactory/</loc></url>
            </urlset>
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication"] = """
                    # DistributedApplication

                    Represents a distributed application.

                    ## Methods

                    - [CreateBuilder(DistributedApplicationOptions)](/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-distributedapplicationoptions) : `DistributedApplicationBuilder` -- Creates a builder.
                    """,
                ["https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplicationbuilder"] = """
                    # DistributedApplicationBuilder

                    Builds a distributed application.

                    ## Constructors

                    - [DistributedApplicationBuilder(DistributedApplicationOptions)](/reference/api/csharp/aspire.hosting/distributedapplicationbuilder/constructors.md#constructor-distributedapplicationoptions) : `DistributedApplicationBuilder` -- Creates a builder instance.
                    """,
                ["https://aspire.dev/reference/api/csharp/aspire.hosting/idistributedapplicationeventingsubscriber"] = """
                    # IDistributedApplicationEventingSubscriber

                    Subscribes to distributed application events.

                    ## Methods

                    - [SubscribeAsync(IDistributedApplicationEventing, DistributedApplicationExecutionContext, CancellationToken)](/reference/api/csharp/aspire.hosting/idistributedapplicationeventingsubscriber/methods.md#subscribeasync-idistributedapplicationeventing-distributedapplicationexecutioncontext-cancellationtoken) : `Task` -- Subscribes to events.
                    """,
                ["https://aspire.dev/reference/api/csharp/aspire.hosting.testing/distributedapplicationfactory"] = """
                    # DistributedApplicationFactory

                    Creates distributed application test hosts.
                    """
            });

        return new ApiDocsIndexService(fetcher, new TestApiDocsCache(), new ConfigurationBuilder().Build(), NullLogger<ApiDocsIndexService>.Instance);
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
            var cachePageUrl = pageUrl.Split('#', 2)[0];
            var markdownUrl = cachePageUrl.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? cachePageUrl : $"{cachePageUrl}.md";
            RequestedMarkdownUrls.Add(markdownUrl);
            return Task.FromResult(pageContent.TryGetValue(cachePageUrl, out var content) ? content : pageContent.TryGetValue(markdownUrl, out content) ? content : null);
        }
    }

    private sealed class SequenceApiDocsFetcher(IEnumerable<string?> sitemapContents) : IApiDocsFetcher
    {
        private readonly Queue<string?> _sitemapContents = new(sitemapContents);

        public Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_sitemapContents.Count > 0 ? _sitemapContents.Dequeue() : null);

        public Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class TestApiDocsCache : IApiDocsCache
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _etags = new(StringComparer.OrdinalIgnoreCase);
        private string? _indexSourceFingerprint;
        private string? _memberIndexSourceFingerprint;
        private string[]? _indexedMemberContainerIds;

        public ApiReferenceItem[]? Index { get; private set; }
        public ApiReferenceItem[]? MemberIndex { get; private set; }

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

        public Task<string?> GetIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_indexSourceFingerprint);

        public Task SetIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            _indexSourceFingerprint = fingerprint;
            return Task.CompletedTask;
        }

        public Task<ApiReferenceItem[]?> GetMemberIndexAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(MemberIndex);

        public Task SetMemberIndexAsync(ApiReferenceItem[] documents, CancellationToken cancellationToken = default)
        {
            MemberIndex = documents;
            return Task.CompletedTask;
        }

        public Task<string?> GetMemberIndexSourceFingerprintAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_memberIndexSourceFingerprint);

        public Task SetMemberIndexSourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            _memberIndexSourceFingerprint = fingerprint;
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
