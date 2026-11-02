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
    [InlineData(true)]
    [InlineData(false)]
    public void Endpoints_DetailsOnlyDescribesDisplayPlacement(bool detailsOnly)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resource = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, urls:
        [
            new("http", new Uri("http://localhost:5000"), isInternal: false, isInactive: false, new("API", 0)),
            new("http", new Uri("http://localhost:5002"), isInternal: true, isInactive: false, new("API", 0)),
            new("diagnostics", new Uri("http://localhost:5001"), isInternal: detailsOnly, isInactive: false, new("Diagnostics", 0))
        ]);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourceOverview(this, viewport,
            new TestDashboardClient(isEnabled: true, initialResources: [resource], resourceChannelProvider: () => channel));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "api");
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() =>
        {
            var endpoints = cut.Find("[aria-labelledby='overview-endpoints-title']");
            var rows = endpoints.QuerySelectorAll("li").OrderBy(row => row.QuerySelector("a")!.GetAttribute("href"), StringComparer.Ordinal);
            Assert.Collection(rows,
                row =>
                {
                    var link = row.QuerySelector("a")!;
                    Assert.Equal("API", link.TextContent);
                    Assert.Equal("http://localhost:5000", link.GetAttribute("href"));
                    Assert.Null(link.GetAttribute("aria-describedby"));
                    Assert.Empty(row.QuerySelectorAll(".overview-badge"));
                },
                row =>
                {
                    var link = row.QuerySelector("a")!;
                    Assert.Equal("Diagnostics", link.TextContent);
                    Assert.Equal("http://localhost:5001", link.GetAttribute("href"));
                    Assert.Equal(detailsOnly ? "overview-endpoint-display-help" : null, link.GetAttribute("aria-describedby"));
                    Assert.Equal(detailsOnly ? ["Details only"] : [], row.QuerySelectorAll(".overview-badge").Select(b => b.TextContent));
                });
            var help = endpoints.QuerySelectorAll("#overview-endpoint-display-help");
            Assert.Equal(detailsOnly ? 1 : 0, help.Length);
            if (detailsOnly)
            {
                Assert.Equal("Details-only endpoints appear in resource details, not summaries. This display setting does not restrict network access.", help[0].TextContent);
            }
        });
    }
}
