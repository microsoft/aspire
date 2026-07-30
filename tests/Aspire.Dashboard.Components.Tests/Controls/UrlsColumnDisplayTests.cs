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
    public void Render_MoreUrlsThanCap_RendersUpToCapInlineAndOverflowsRest()
    {
        // Arrange
        // 30 URLs exceeds the 20-item safety cap, so 20 render inline and the remaining 10 are
        // always in the overflow popover (pre-overflowed past the cap).
        const int totalUrls = 30;
        const int cap = 20;

        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

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
        // Up to the cap is rendered inline for the measurer to size; the first is always shown fixed.
        var inlineItems = cut.FindAll(".url-overflow-item");
        Assert.Equal(cap, inlineItems.Count);
        Assert.Single(cut.FindAll(".url-overflow-first"));
        Assert.Equal("Endpoint 0", cut.Find(".url-overflow-first a").TextContent);

        // Before measurement, everything rendered is assumed to fit, so the button counts only the
        // items past the cap.
        var overflowButton = cut.Find(".url-button");
        Assert.Equal($"+{totalUrls - cap}", overflowButton.TextContent.Trim());

        // The popover lists exactly the pre-overflowed (past-cap) items.
        overflowButton.Click();
        var popoverLinks = cut.FindAll(".url-popup .url-link");
        Assert.Equal(totalUrls - cap, popoverLinks.Count);
        Assert.Equal("Endpoint 20", popoverLinks[0].TextContent);
    }

    [Fact]
    public void Render_FewUrlsAllFit_NoOverflowButton()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(5);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        // Act
        var cut = RenderComponent<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Assert
        // All five are rendered inline and, with nothing measured as overflowing, there is no button.
        Assert.Equal(5, cut.FindAll(".url-overflow-item").Count);
        Assert.Empty(cut.FindAll(".url-button"));
    }

    [Fact]
    public async Task SetVisibleCount_CollapsesMeasuredOverflowIntoPopover()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(5);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        var cut = RenderComponent<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Act
        // Emulate the JS measurer reporting that only the first two of five items fit.
        await cut.InvokeAsync(() => cut.Instance.SetVisibleCountAsync(2));

        // Assert
        // All items stay in the DOM (the measurer toggles inline visibility), but the button and
        // popover now reflect the three overflowed items.
        Assert.Equal(5, cut.FindAll(".url-overflow-item").Count);
        Assert.Equal("+3", cut.Find(".url-button").TextContent.Trim());

        cut.Find(".url-button").Click();
        var popoverLinks = cut.FindAll(".url-popup .url-link");
        Assert.Equal(3, popoverLinks.Count);
        Assert.Equal("Endpoint 2", popoverLinks[0].TextContent);
        Assert.Equal("Endpoint 4", popoverLinks[2].TextContent);
    }

    [Fact]
    public async Task SetVisibleCount_CombinesMeasuredOverflowWithPastCapItems()
    {
        // Arrange
        const int totalUrls = 30;
        const int cap = 20;

        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(totalUrls);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        var cut = RenderComponent<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Act
        // Only the first five rendered items fit; the count must include both the 15 measured-overflow
        // rendered items and the 10 items that live past the cap.
        await cut.InvokeAsync(() => cut.Instance.SetVisibleCountAsync(5));

        // Assert
        var expectedOverflow = (cap - 5) + (totalUrls - cap);
        Assert.Equal($"+{expectedOverflow}", cut.Find(".url-button").TextContent.Trim());

        cut.Find(".url-button").Click();
        Assert.Equal(expectedOverflow, cut.FindAll(".url-popup .url-link").Count);
        Assert.Equal("Endpoint 5", cut.FindAll(".url-popup .url-link")[0].TextContent);
    }

    [Fact]
    public void Render_SingleUrl_ShowsNoOverflowButton()
    {
        // Arrange
        JSInterop.Mode = JSRuntimeMode.Loose;
        DashboardSetupHelpers.AddCommonDashboardServices(this);

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
