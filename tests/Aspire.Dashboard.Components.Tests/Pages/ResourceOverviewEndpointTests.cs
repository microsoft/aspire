// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewEndpointTests : DashboardTestContext
{
    [Theory]
    [InlineData("https://user:password@localhost:7443/catalog?token=secret#access_token=fragment", "https://localhost:7443/catalog?token=MASK", true)]
    [InlineData("redis://:password@localhost:6379", "redis://localhost:6379", false)]
    [InlineData("https://localhost:7443/catalog#access_token=fragment", "https://localhost:7443/catalog", true)]
    [InlineData("tcp://user:password@localhost:6379/?token=secret#fragment", "tcp://localhost:6379/?token=MASK", false)]
    [InlineData("https://localhost:7443/catalog?token=secret", "https://localhost:7443/catalog?token=MASK", true)]
    public void Endpoints_DisplaySafeAddressesAndPreserveDestinations(string original, string expected, bool isLink)
    {
        expected = expected.Replace("MASK", new string('\u25cf', 8));
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resource = ModelTestHelpers.CreateResource("catalog", state: KnownResourceState.Running, urls:
        [
            new("public", new Uri(original), false, false, new("Catalog API", 5)),
            new("public", new Uri(original), true, false, new("Catalog API", 5)),
            new("plain", new Uri(original), false, false, UrlDisplayPropertiesViewModel.Empty)
        ]);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, new TestDashboardClient(
            isEnabled: true, initialResources: [resource], resourceChannelProvider: () => channel));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/catalog");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "catalog");
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find("[aria-labelledby='overview-endpoints-title']");
            var rows = card.QuerySelectorAll(".overview-endpoint");
            Assert.Equal(2, rows.Length);
            var named = rows.Single(row => row.QuerySelector(".overview-endpoint-name") is not null);
            Assert.Equal("Catalog API", named.QuerySelector(".overview-endpoint-name")!.TextContent);
            Assert.Equal(3, card.QuerySelectorAll(".overview-endpoint-address").Length);
            Assert.All(card.QuerySelectorAll(".overview-endpoint-address"), address =>
            {
                Assert.Equal(expected, address.TextContent);
                Assert.Equal(expected, address.GetAttribute("data-tooltip"));
            });
            Assert.All(rows, row =>
            {
                var primary = row.QuerySelector(".overview-endpoint-address")!;
                Assert.Equal(isLink ? "A" : "SPAN", primary.TagName);
                if (isLink)
                {
                    Assert.Equal(original, primary.GetAttribute("href"));
                    Assert.Equal("_blank", primary.GetAttribute("target"));
                    Assert.Equal("noreferrer noopener", primary.GetAttribute("rel"));
                }
                else
                {
                    Assert.Equal("0", primary.GetAttribute("tabindex"));
                    Assert.Null(primary.GetAttribute("href"));
                }
            });
            var secondary = named.QuerySelectorAll(".overview-endpoint-address")[1];
            Assert.Equal("SPAN", secondary.TagName);
            Assert.Equal("0", secondary.GetAttribute("tabindex"));
            Assert.Null(secondary.GetAttribute("href"));
        });
    }
}
