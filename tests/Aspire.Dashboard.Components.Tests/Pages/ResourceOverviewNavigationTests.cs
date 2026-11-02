// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewNavigationTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamedResource_OverviewLinksUseTechnicalNames(bool replicas)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        List<ResourceViewModel> resources =
        [
            ModelTestHelpers.CreateResource("cache-1", displayName: "cache", resourceDisplayName: "Shared Cache",
                relationships: [new RelationshipViewModel("worker", "Reference")]),
            ModelTestHelpers.CreateResource("worker-instance", displayName: "worker", resourceDisplayName: "Worker service")
        ];
        if (replicas)
        {
            resources.Add(ModelTestHelpers.CreateResource("cache-2", displayName: "cache", resourceDisplayName: "Shared Cache"));
            resources.Add(ModelTestHelpers.CreateResource("worker-instance-2", displayName: "worker", resourceDisplayName: "Worker service"));
        }
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: resources, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var target = replicas ? "cache-1" : "cache";
        navigation.NavigateTo(DashboardUrls.ResourceOverviewUrl(target));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), target);
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() =>
        {
            var links = cut.FindAll(".relationship-graph-node[href]");
            var expected = replicas
                ? new[]
                {
                    ("/resources/worker-instance", "Reference: Worker service (worker-instance)"),
                    ("/resources/worker-instance-2", "Reference: Worker service (worker-instance-2)")
                }
                : [("/resources/worker", "Reference: Worker service")];
            Assert.Equal(expected, links.Select(link => (Assert.IsType<string>(link.GetAttribute("href")), Assert.IsType<string>(link.GetAttribute("aria-label")))));
        });

        var openConsole = cut.Find("[aria-labelledby='overview-console-title'] [aria-label='Open console']");
        await cut.InvokeAsync(() => openConsole.Click());
        Assert.Equal("http://localhost" + DashboardUrls.ConsoleLogsUrl(target), navigation.Uri);
        Assert.Equal([target], cut.Instance.SelectedResourceNames);
    }
}
