// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsSourceConfigurationTests
{
    [Fact]
    public void GetSitemapUrl_PrefersAspireConfigDocsApiPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiDocsSourceConfiguration.SitemapUrlConfigPath] = "http://localhost:4321/sitemap-0.xml"
            })
            .Build();

        var sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);

        Assert.Equal("http://localhost:4321/sitemap-0.xml", sitemapUrl);
    }

    [Fact]
    public void GetSitemapUrl_DefaultsToBuiltInSource()
    {
        var configuration = new ConfigurationBuilder().Build();

        var sitemapUrl = ApiDocsSourceConfiguration.GetSitemapUrl(configuration);

        Assert.Equal(ApiDocsSourceConfiguration.DefaultSitemapUrl, sitemapUrl);
    }

    [Fact]
    public void GetSitemapContentCacheKey_DefaultSourceUsesFriendlyStem()
    {
        var cacheKey = ApiDocsSourceConfiguration.GetSitemapContentCacheKey("https://aspire.dev/sitemap-0.xml");

        Assert.Equal("sitemap-0", cacheKey);
    }

    [Fact]
    public void GetIndexCacheKey_UsesConfiguredSourceUrl()
    {
        var aspireDevKey = ApiDocsSourceConfiguration.GetIndexCacheKey("https://aspire.dev/sitemap-0.xml");
        var localhostKey = ApiDocsSourceConfiguration.GetIndexCacheKey("http://localhost:4321/sitemap-0.xml");

        Assert.Equal("index:sitemap-0", aspireDevKey);
        Assert.NotEqual(aspireDevKey, localhostKey);
    }

    [Fact]
    public void BuildMarkdownUrl_RebasesConfiguredHost()
    {
        var markdownUrl = ApiDocsSourceConfiguration.BuildMarkdownUrl(
            "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods",
            "http://localhost:4321/sitemap-0.xml");

        Assert.Equal("http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md", markdownUrl);
    }

    [Fact]
    public void BuildMarkdownUrl_StripsFragmentFromAnchoredMemberUrl()
    {
        var markdownUrl = ApiDocsSourceConfiguration.BuildMarkdownUrl(
            "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods#dothing-string",
            "https://aspire.dev/sitemap-0.xml");

        Assert.Equal("https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods.md", markdownUrl);
    }

    [Fact]
    public void GetPageContentCacheKey_DefaultHostOmitsSchemeAndHost()
    {
        var cacheKey = ApiDocsSourceConfiguration.GetPageContentCacheKey(
            "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods",
            "https://aspire.dev/sitemap-0.xml");

        Assert.Equal("reference-api-csharp-aspire.test.package-testtype-methods", cacheKey);
    }

    [Fact]
    public void GetMemberIndexCacheKey_UsesConfiguredSourceUrl()
    {
        var aspireDevKey = ApiDocsSourceConfiguration.GetMemberIndexCacheKey("https://aspire.dev/sitemap-0.xml");
        var localhostKey = ApiDocsSourceConfiguration.GetMemberIndexCacheKey("http://localhost:4321/sitemap-0.xml");

        Assert.Equal("member-index:sitemap-0", aspireDevKey);
        Assert.NotEqual(aspireDevKey, localhostKey);
    }

    [Fact]
    public void ResolveLinkedPageUrl_RebasesConfiguredHostAndStripsMarkdownAndFragment()
    {
        var pageUrl = ApiDocsSourceConfiguration.ResolveLinkedPageUrl(
            "/reference/api/csharp/aspire.test.package/testtype/methods.md#dothing-string",
            "http://localhost:4321/sitemap-0.xml");

        Assert.Equal("http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods", pageUrl);
    }

    [Fact]
    public void RewriteMarkdownLinks_RebasesConfiguredHostAndPreservesAnchors()
    {
        var rewritten = ApiDocsSourceConfiguration.RewriteMarkdownLinks(
            """
            See [Package](/reference/api/csharp/aspire.test.package.md), [Member](/reference/api/csharp/aspire.test.package/testtype/methods.md#dothing-string), [CreateBuilder(string[])](/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string), and [This section](#remarks).
            """,
            "https://aspire.dev/reference/api/csharp/aspire.test.package/testtype",
            "http://localhost:4321/sitemap-0.xml");

        Assert.Equal(
            "See [Package](http://localhost:4321/reference/api/csharp/aspire.test.package.md), [Member](http://localhost:4321/reference/api/csharp/aspire.test.package/testtype/methods.md#dothing-string), [CreateBuilder(string[])](http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string), and [This section](http://localhost:4321/reference/api/csharp/aspire.test.package/testtype#remarks).",
            rewritten);
    }

    [Fact]
    public void RewriteMarkdownLinks_RewritesBracketedMemberSignatureLinksFromDistributedApplicationPage()
    {
        var rewritten = ApiDocsSourceConfiguration.RewriteMarkdownLinks(
            """
            ## Methods

            - [CreateBuilder(string[])](/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string) : [IDistributedApplicationBuilder](/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md) `static` `ats export` -- Creates a new instance of [IDistributedApplicationBuilder](/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md) with the specified command-line arguments.
            """,
            "https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication",
            "http://localhost:4321/sitemap-0.xml");

        Assert.Contains("[CreateBuilder(string[])](http://localhost:4321/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)", rewritten, StringComparison.Ordinal);
        Assert.Contains("[IDistributedApplicationBuilder](http://localhost:4321/reference/api/csharp/aspire.hosting/idistributedapplicationbuilder.md)", rewritten, StringComparison.Ordinal);
    }
}
