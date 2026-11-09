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
        Assert.Equal(displayedUrls.Skip(20).Select(url => url.Text), popupItems.Select(item => item.TextContent.Trim()));
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

    [Fact]
    public void Render_MasksAddressesButLinksToThemAndLetsTextAddressesTakeFocus()
    {
        const string link = "https://localhost:5000/login?token=synthetic-secret";
        const string tcp = "tcp://localhost:5432?password=synthetic-secret";
        const string other = "https://localhost:5001/?key=synthetic-secret";

        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", resourceType: "Project", state: KnownResourceState.Running);

        var cut = Render<UrlsColumnDisplay>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.HasMultipleReplicas, false);
            builder.Add(p => p.DisplayedUrls, new List<DisplayedUrl>
            {
                new() { Index = 0, Name = "https", Text = link, Url = link, OriginalUrlString = link },
                new() { Index = 1, Name = "tcp", Text = tcp, Url = null, OriginalUrlString = tcp },
                new() { Index = 2, Name = "other", Text = other, Url = other, OriginalUrlString = other }
            });
        });

        // A query value can be a secret, so the text and tooltip mask it, but the link opens the full address.
        var anchor = cut.FindAll("fluent-overflow > div a").First(a => a.GetAttribute("href") == link);
        Assert.Equal("https://localhost:5000/login?token=●●●●●●●●", anchor.TextContent);
        Assert.Equal("https://localhost:5000/login?token=●●●●●●●●", anchor.GetAttribute("data-tooltip"));

        // An address that isn't a link takes focus so its full value can be read in the tooltip.
        var text = cut.Find("fluent-overflow > div span[tabindex]");
        Assert.Equal("tcp://localhost:5432?password=●●●●●●●●", text.TextContent);
        Assert.Equal("tcp://localhost:5432?password=●●●●●●●●", text.GetAttribute("data-tooltip"));
        Assert.Equal("tcp://localhost:5432?password=●●●●●●●●", text.GetAttribute("aria-label"));
        Assert.Equal("0", text.GetAttribute("tabindex"));
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
