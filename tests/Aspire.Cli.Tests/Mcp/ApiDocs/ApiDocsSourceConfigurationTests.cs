// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiDocsSourceConfigurationTests
{
    [Fact]
    public void GetIndexCacheKey_UsesConfiguredSourceUrl()
    {
        var aspireDevKey = ApiDocsSourceConfiguration.GetIndexCacheKey("https://aspire.dev/sitemap-0.xml");
        var localhostKey = ApiDocsSourceConfiguration.GetIndexCacheKey("http://localhost:4321/sitemap-0.xml");

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
}