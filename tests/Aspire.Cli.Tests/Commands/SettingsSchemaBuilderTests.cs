// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Documentation.Docs;

namespace Aspire.Cli.Tests.Commands;

public class SettingsSchemaBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildSchema_IncludesNamedSkillSources(bool global)
    {
        foreach (var schema in new[]
        {
            SettingsSchemaBuilder.BuildSchema(excludeLocalOnly: global),
            SettingsSchemaBuilder.BuildConfigFileSchema(excludeLocalOnly: global)
        })
        {
            var skillSources = Assert.Single(schema.Properties, property => property.Name == "skillSources");
            Assert.Equal("object", skillSources.AdditionalPropertiesType);
            Assert.Equal(
                ["enabled", "repository", "marketplace", "plugins", "ref", "openCodeV1Catalog", "openCodeV2Catalog"],
                skillSources.SubProperties!.Select(property => property.Name));
            Assert.Equal("array", Assert.Single(skillSources.SubProperties!, property => property.Name == "plugins").Type);
        }
    }

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

    [Fact]
    public void BuildSchema_DoesNotIncludeLegacyAppHostPath()
    {
        var schema = SettingsSchemaBuilder.BuildSchema(excludeLocalOnly: false);

        Assert.DoesNotContain(schema.Properties, static property => property.Name == "appHostPath");
    }

    [Fact]
    public void BuildConfigFileSchema_KeepsLocalAppHostPath()
    {
        var schema = SettingsSchemaBuilder.BuildConfigFileSchema(excludeLocalOnly: false);

        var appHostProperty = Assert.Single(schema.Properties, static property => property.Name == "appHost");
        Assert.NotNull(appHostProperty.SubProperties);
        Assert.Contains(appHostProperty.SubProperties, static property => property.Name == "path");
    }
}
