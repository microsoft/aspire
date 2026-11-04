// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class EmptyStateDocumentationTests : DashboardTestContext
{
    [Theory]
    [InlineData(EmptyStateDocumentationPanel.Endpoints, "Inner-loop networking overview", "https://aspire.dev/fundamentals/networking-overview/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#how-endpoints-work")]
    [InlineData(EmptyStateDocumentationPanel.Console, "Resource Model", "https://aspire.dev/architecture/resource-model/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#resource-logging")]
    [InlineData(EmptyStateDocumentationPanel.StructuredLogs, "C# Service Defaults", "https://aspire.dev/get-started/csharp-service-defaults/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#opentelemetry-configuration")]
    [InlineData(EmptyStateDocumentationPanel.Traces, "C# Service Defaults", "https://aspire.dev/get-started/csharp-service-defaults/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#opentelemetry-configuration")]
    [InlineData(EmptyStateDocumentationPanel.Metrics, "C# Service Defaults", "https://aspire.dev/get-started/csharp-service-defaults/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#opentelemetry-configuration")]
    [InlineData(EmptyStateDocumentationPanel.Relationships, "Build custom Aspire resources", "https://aspire.dev/extensibility/custom-resources/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#resource-relationships")]
    [InlineData(EmptyStateDocumentationPanel.Properties, "Build custom Aspire resources", "https://aspire.dev/extensibility/custom-resources/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#initial-state-configuration")]
    [InlineData(EmptyStateDocumentationPanel.EnvironmentVariables, "Environment variables", "https://aspire.dev/fundamentals/environment-variables/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state")]
    [InlineData(EmptyStateDocumentationPanel.Volumes, "Persist Aspire data with volumes and binds", "https://aspire.dev/fundamentals/persist-data-volumes/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state")]
    [InlineData(EmptyStateDocumentationPanel.HealthChecks, "Health checks", "https://aspire.dev/fundamentals/health-checks/?utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#apphost-resource-health-checks")]
    public void Render_ArticleMapping_RendersExpectedAccessibleLink(EmptyStateDocumentationPanel panel, string title, string url)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = Render<EmptyStateDocumentation>(parameters => parameters
            .Add(p => p.Message, "Empty message")
            .Add(p => p.Panel, panel));

        var link = cut.Find("a");
        Assert.Equal(title, link.TextContent.Trim());
        Assert.Equal(url, link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("Empty message", cut.Find(".empty-state-documentation-message").TextContent);
        Assert.Single(cut.FindAll(".empty-state-documentation-footer a"));
        Assert.Equal(url, cut.Find(".empty-state-documentation-footer a").GetAttribute("href"));
    }

    [Fact]
    public void AddAttribution_ExistingQueryAndFragment_PreservesBoth()
    {
        var url = EmptyStateDocumentationLinks.AddAttribution("https://example.com/article?language=en#section");

        Assert.Equal(
            "https://example.com/article?language=en&utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state#section",
            url);
    }

    [Fact]
    public void GetArticle_DuplicateTargets_AreLimitedToTelemetryConfiguration()
    {
        var mappings = Enum.GetValues<EmptyStateDocumentationPanel>()
            .Select(panel => (Panel: panel, Article: EmptyStateDocumentationLinks.GetArticle(panel)))
            .ToList();

        var duplicate = Assert.Single(mappings.GroupBy(mapping => mapping.Article.Url), group => group.Count() > 1);
        Assert.Equal(
            [EmptyStateDocumentationPanel.StructuredLogs, EmptyStateDocumentationPanel.Traces, EmptyStateDocumentationPanel.Metrics],
            duplicate.Select(mapping => mapping.Panel));
        Assert.EndsWith("#opentelemetry-configuration", duplicate.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void GetArticle_AllUrls_PlaceAttributionBeforeFragment()
    {
        foreach (var panel in Enum.GetValues<EmptyStateDocumentationPanel>())
        {
            var url = EmptyStateDocumentationLinks.GetArticle(panel).Url;
            var fragmentIndex = url.IndexOf('#', StringComparison.Ordinal);
            var attributionIndex = url.IndexOf("utm_source=aspire-dashboard&utm_medium=product&utm_campaign=resource-empty-state", StringComparison.Ordinal);

            Assert.True(attributionIndex >= 0);
            Assert.True(fragmentIndex < 0 || attributionIndex < fragmentIndex);
        }
    }
}
