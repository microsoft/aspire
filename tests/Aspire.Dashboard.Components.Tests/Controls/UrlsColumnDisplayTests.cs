// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class UrlsColumnDisplayTests : DashboardTestContext
{
    [Fact]
    public void Render_MultipleUrls_ShowsFirstUrlInlineAndOverflowButton()
    {
        // Arrange
        const int totalUrls = 30;

        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(totalUrls);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        // Act
        var cut = RenderComponent<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Assert
        // Only the first URL is rendered inline; the rest are collapsed behind the overflow button.
        // This keeps DOM element count low so a large URL set can't trigger a reflow that drops the
        // SignalR connection.
        var inlineLinks = cut.FindAll(".url-overflow-first a");
        Assert.Single(inlineLinks);
        Assert.Equal("Endpoint 0", inlineLinks[0].TextContent);

        var overflowButton = cut.Find(".url-button");
        Assert.Equal($"+{totalUrls - 1}", overflowButton.TextContent.Trim());
    }

    [Fact]
    public void Render_SingleUrl_ShowsNoOverflowButton()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(1);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        // Act
        var cut = RenderComponent<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Assert
        Assert.Empty(cut.FindAll(".url-button"));
        var links = cut.FindAll(".url-container a");
        Assert.Single(links);
        Assert.Equal("Endpoint 0", links[0].TextContent);
    }

    private static List<DisplayedUrl> CreateDisplayedUrls(int count)
    {
        return Enumerable.Range(0, count).Select(i => new DisplayedUrl
        {
            Index = i,
            Name = $"https-{i}",
            Text = $"Endpoint {i}",
            Url = $"https://localhost:{5000 + i}",
            OriginalUrlString = $"https://localhost:{5000 + i}"
        }).ToList<DisplayedUrl>();
    }
}
