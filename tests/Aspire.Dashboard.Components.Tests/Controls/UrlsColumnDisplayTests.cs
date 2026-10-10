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
    public void Render_MoreThanMaxRenderedItems_RendersBoundedPayload()
    {
        // Arrange
        const int totalUrls = 30;

        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = CreateDisplayedUrls(totalUrls);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        // Act
        var cut = Render<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Assert
        var overflow = cut.Find("fluent-overflow");
        var overflowItems = cut.FindAll("fluent-overflow > div:not(.fluent-overflow-more)");
        Assert.Equal("10", overflow.GetAttribute("pre-overflow-count"));
        Assert.Equal("0", overflow.GetAttribute("threshold"));
        Assert.Equal("ellipsis", overflowItems[0].GetAttribute("behavior"));
        Assert.All(overflowItems.Skip(1), item => Assert.Null(item.GetAttribute("behavior")));
        Assert.Equal(20, overflowItems.Count);
        Assert.Equal("+10", cut.Find(".fluent-overflow-more fluent-button").TextContent.Trim());

        var popupItems = cut.FindAll(".url-overflow-popover .url-link");
        Assert.Equal(displayedUrls.Skip(20).Select(url => $"{url.Name}: {url.Url}"), popupItems.Select(item => item.TextContent.Trim()));
    }

    [Fact]
    public void Render_OverflowPopover_ShowsLabelAndAddressLink()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        // 20 items render inline, so the last 5 are the ones shown in the popover.
        var displayedUrls = CreateDisplayedUrls(25);
        displayedUrls[21].DisplayName = "Display name";
        displayedUrls[22].Name = "-";
        displayedUrls[23].Name = "-";
        displayedUrls[23].DisplayName = "Only display name";
        displayedUrls[24].Url = null;
        displayedUrls[24].OriginalUrlString = "tcp://localhost:5024";
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        var cut = Render<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // The label ("{display name ?? endpoint name}: ") is plain text so only the address is the link. The address
        // is always a link, including for schemes that aren't browsable, and the label is omitted if there isn't one.
        var popupItems = cut.FindAll(".url-overflow-popover .url-link");
        Assert.Collection(popupItems,
            item => AssertPopupItem(item, "https-20: https://localhost:5020", "https://localhost:5020"),
            item => AssertPopupItem(item, "Display name: https://localhost:5021", "https://localhost:5021"),
            item => AssertPopupItem(item, "https://localhost:5022", "https://localhost:5022"),
            item => AssertPopupItem(item, "Only display name: https://localhost:5023", "https://localhost:5023"),
            item => AssertPopupItem(item, "https-24: tcp://localhost:5024", "tcp://localhost:5024"));

        static void AssertPopupItem(AngleSharp.Dom.IElement item, string expectedText, string expectedAddress)
        {
            Assert.Equal(expectedText, item.TextContent.Trim());

            var link = Assert.Single(item.QuerySelectorAll("a"));
            Assert.Equal(expectedAddress, link.TextContent);
            Assert.Equal(expectedAddress, link.GetAttribute("href"));
        }
    }

    [Fact]
    public void Render_SingleNonBrowsableUrl_RendersLinkWithDisplayText()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var displayedUrls = new List<DisplayedUrl>
        {
            new() { Index = 0, Name = "tcp", Text = "tcp (5000)", Url = null, OriginalUrlString = "tcp://localhost:5000" }
        };
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        var cut = Render<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, displayedUrls);
        });

        // Text is what's displayed, the address is the target (and tooltip), even when the scheme isn't browsable.
        var link = cut.Find(".url-container a");
        Assert.Equal("tcp (5000)", link.TextContent);
        Assert.Equal("tcp://localhost:5000", link.GetAttribute("href"));
        Assert.Equal("tcp://localhost:5000", link.GetAttribute("title"));
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
        var cut = Render<UrlsColumnDisplay>(builder =>
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
