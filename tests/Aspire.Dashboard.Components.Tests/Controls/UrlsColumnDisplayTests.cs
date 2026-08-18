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
    public void Render_MoreThanMaxRenderedItems_RendersAllSourceItemsWithBoundedPayload()
    {
        // Arrange
        const int totalUrls = 30;

        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentOverflow(this);
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
    var overflow = cut.Find("fluent-overflow");
        var overflowItems = cut.FindAll("fluent-overflow > div:not(.fluent-overflow-more)");
    Assert.Equal("20", overflow.GetAttribute("max-rendered-items"));
    Assert.Equal(totalUrls, overflowItems.Count);
    }

    [Fact]
    public void Render_ExactlyMaxUrls_RendersAllItems()
    {
        // Arrange
        const int totalUrls = 20;

        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentOverflow(this);
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
        var overflowItems = cut.FindAll("fluent-overflow > div:not(.fluent-overflow-more)");
        Assert.Equal(totalUrls, overflowItems.Count);
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
