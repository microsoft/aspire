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
    public void Parse_FiltersToGoApiRoutes()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/getting-started/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/reference/api/typescript/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/da/reference/api/go/aspire.hosting/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        Assert.Collection(
            result,
            entry =>
            {
                Assert.Equal("go", entry.Language);
                Assert.Equal(["aspire.hosting"], entry.Segments);
            },
            entry =>
            {
                Assert.Equal("typescript", entry.Language);
                Assert.Equal(["aspire.hosting"], entry.Segments);
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
    public void Parse_ParsesGoHierarchySegments()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/go/aspire.hosting.redis/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go/aspire.hosting.redis/redisresource/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go/aspire.hosting.redis/redisresource/withredisinsight/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        Assert.Collection(
            result,
            entry =>
            {
                Assert.Equal(ApiReferenceLanguages.Go, entry.Language);
                Assert.Equal("https://aspire.dev/reference/api/go/aspire.hosting.redis", entry.Url);
                Assert.Equal(["aspire.hosting.redis"], entry.Segments);
            },
            entry =>
            {
                Assert.Equal(ApiReferenceLanguages.Go, entry.Language);
                Assert.Equal("https://aspire.dev/reference/api/go/aspire.hosting.redis/redisresource", entry.Url);
                Assert.Equal(["aspire.hosting.redis", "redisresource"], entry.Segments);
            },
            entry =>
            {
                Assert.Equal(ApiReferenceLanguages.Go, entry.Language);
                Assert.Equal("https://aspire.dev/reference/api/go/aspire.hosting.redis/redisresource/withredisinsight", entry.Url);
                Assert.Equal(["aspire.hosting.redis", "redisresource", "withredisinsight"], entry.Segments);
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

    [Fact]
    public void Parse_DoesNotMatchPartialGoLanguagePrefixes()
    {
        var sitemap = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://aspire.dev/reference/api/gopher/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/reference/api/golang/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go-extra/aspire.hosting/</loc></url>
              <url><loc>https://aspire.dev/reference/api/go/aspire.hosting/</loc></url>
            </urlset>
            """;

        var result = ApiSitemapParser.Parse(sitemap);

        var entry = Assert.Single(result);
        Assert.Equal(ApiReferenceLanguages.Go, entry.Language);
        Assert.Equal(["aspire.hosting"], entry.Segments);
    }
}
