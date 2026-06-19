// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiSitemapParserTests
{
    [Fact]
    public void Parse_WithEmptyContent_ReturnsEmptyList()
    {
        var result = ApiSitemapParser.Parse("");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_FiltersToSupportedApiRoutes()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/getting-started/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.test/</loc></url>
              <url><loc>https://aspire.dev/da/reference/api/csharp/aspire.test.package/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        Assert.Collection(
            result,
            entry =>
            {
                Assert.Equal("csharp", entry.Language);
                Assert.Equal(["aspire.test.package"], entry.Segments);
            },
            entry =>
            {
                Assert.Equal("typescript", entry.Language);
                Assert.Equal(["aspire.hosting.test"], entry.Segments);
            });
    }

    [Fact]
    public void Parse_ParsesHierarchySegments()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting.test/testresource/runasemulator/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        Assert.Collection(
            result,
            entry =>
            {
                Assert.Equal("https://aspire.dev/reference/api/csharp/aspire.test.package/testtype/methods", entry.Url);
                Assert.Equal(["aspire.test.package", "testtype", "methods"], entry.Segments);
            },
            entry =>
            {
                Assert.Equal("https://aspire.dev/reference/api/typescript/aspire.hosting.test/testresource/runasemulator", entry.Url);
                Assert.Equal(["aspire.hosting.test", "testresource", "runasemulator"], entry.Segments);
             });
    }

    [Fact]
    public void Parse_DoesNotMatchPartialLanguagePrefixes()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/csharply/aspire.test.package/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript-extra/aspire.hosting.test/</loc></url>
              <url><loc>https://aspire.dev/reference/api/csharp/aspire.test.package/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        var entry = Assert.Single(result);
        Assert.Equal(ApiReferenceLanguages.CSharp, entry.Language);
        Assert.Equal(["aspire.test.package"], entry.Segments);
    }
}
