// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewRelationshipGraphTests : DashboardTestContext
{
    [Fact]
    public async Task Labels_TrimAtEndUnlessResourceHasMultipleInstances()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var changes = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("catalogservice-production",
                    relationships: [new("catalogdatabase-production", "Reference")]),
                ModelTestHelpers.CreateResource("frontendservice-production-abc1234", displayName: "frontendservice-production",
                    relationships: [new("catalogservice-production", "Reference")]),
                ModelTestHelpers.CreateResource("catalogdatabase-production-abc1234", displayName: "catalogdatabase-production")
            ],
            resourceChannelProvider: () => changes);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/catalogservice-production");

        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "catalogservice-production");
            builder.CloseComponent();
        }));

        string[] singletonLabels = ["frontendservice…", "catalogservice-…", "catalogdatabase…"];
        cut.WaitForAssertion(() => Assert.Equal(singletonLabels,
            cut.FindAll(".relationship-graph-node text").Select(n => n.TextContent)));

        // The center's other instance has no relationships, so it isn't a node in this graph.
        var replicas = new[]
        {
            ModelTestHelpers.CreateResource("catalogservice-production-second", displayName: "catalogservice-production"),
            ModelTestHelpers.CreateResource("frontendservice-production-xyz9876", displayName: "frontendservice-production",
                relationships: [new("catalogservice-production", "Reference")]),
            ModelTestHelpers.CreateResource("catalogdatabase-production-xyz9876", displayName: "catalogdatabase-production")
        };
        await changes.Writer.WriteAsync(replicas.Select(r => new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, r)).ToArray());

        cut.WaitForAssertion(() => Assert.Equal(
            ["frontend…abc1234", "frontend…xyz9876", "catalogs…duction", "catalogd…abc1234", "catalogd…xyz9876"],
            cut.FindAll(".relationship-graph-node text").Select(n => n.TextContent)));
        var outgoing = cut.Find(".relationship-graph-node[href='/resources/catalogdatabase-production-abc1234']");
        Assert.Equal("Reference: catalogdatabase-production-abc1234", outgoing.GetAttribute("aria-label"));
        Assert.Equal("Reference: catalogdatabase-production-abc1234", outgoing.GetAttribute("data-tooltip"));

        await changes.Writer.WriteAsync(replicas.Select(r => new ResourceViewModelChange(ResourceViewModelChangeType.Delete, r)).ToArray());

        cut.WaitForAssertion(() => Assert.Equal(singletonLabels,
            cut.FindAll(".relationship-graph-node text").Select(n => n.TextContent)));
    }
}
