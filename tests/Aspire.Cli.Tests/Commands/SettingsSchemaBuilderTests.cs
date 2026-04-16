// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Documentation.Docs;

namespace Aspire.Cli.Tests.Commands;

public class SettingsSchemaBuilderTests
{
    [Fact]
    public void BuildSchema_DoesNotIncludeDocsSourceProperties()
    {
        var schema = SettingsSchemaBuilder.BuildSchema(excludeLocalOnly: false);

        Assert.DoesNotContain(schema.Properties, static property => property.Name == "docs");
    }

    [Fact]
    public void BuildConfigFileSchema_IncludesDocsSourcePropertiesWithDefaults()
    {
        var schema = SettingsSchemaBuilder.BuildConfigFileSchema(excludeLocalOnly: false);

        var docsProperty = Assert.Single(schema.Properties, static property => property.Name == "docs");
        Assert.NotNull(docsProperty.SubProperties);
        Assert.Contains("override", docsProperty.Description, StringComparison.OrdinalIgnoreCase);

        var llmsProperty = Assert.Single(docsProperty.SubProperties, static property => property.Name == "llmsTxtUrl");
        Assert.Equal("string", llmsProperty.Type);
        Assert.Contains(DocsSourceConfiguration.DefaultLlmsTxtUrl, llmsProperty.Description, StringComparison.Ordinal);
        Assert.Contains("override", llmsProperty.Description, StringComparison.OrdinalIgnoreCase);

        var apiProperty = Assert.Single(docsProperty.SubProperties, static property => property.Name == "api");
        Assert.NotNull(apiProperty.SubProperties);
        Assert.Contains("override", apiProperty.Description, StringComparison.OrdinalIgnoreCase);

        var sitemapProperty = Assert.Single(apiProperty.SubProperties, static property => property.Name == "sitemapUrl");
        Assert.Equal("string", sitemapProperty.Type);
        Assert.Contains(ApiDocsSourceConfiguration.DefaultSitemapUrl, sitemapProperty.Description, StringComparison.Ordinal);
        Assert.Contains("override", sitemapProperty.Description, StringComparison.OrdinalIgnoreCase);
    }
}
