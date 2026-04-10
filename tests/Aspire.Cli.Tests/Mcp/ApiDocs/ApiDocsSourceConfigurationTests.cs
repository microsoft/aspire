// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsSourceConfigurationTests
{
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
}
