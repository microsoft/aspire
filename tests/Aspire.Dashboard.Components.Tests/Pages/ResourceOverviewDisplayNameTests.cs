// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
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
public class ResourceOverviewDisplayNameTests : DashboardTestContext
{
    [Fact]
    public void Overview_LabeledResourceDisplaysLabelsAndTargetsResourceName()
    {
        var cut = RenderOverview("/resources/postgres", "postgres");

        cut.WaitForAssertion(() => Assert.Equal(["postgres"], cut.Instance.SelectedResourceNames));
        Assert.Equal("postgres", cut.Instance.SelectedResource?.Name);

        var rowLink = cut.Find("[data-resource-name='postgres'] .resource-row-link");
        Assert.EndsWith("/resources/postgres", rowLink.GetAttribute("href"));
        Assert.Equal("Orders Database", rowLink.GetAttribute("aria-label"));
        Assert.Equal("Orders Database", cut.Find("[data-resource-name='postgres'] .resource-row-name").TextContent.Trim());

        var status = cut.Find("[aria-labelledby='overview-status-title']");
        var values = status.QuerySelectorAll("dt").ToDictionary(dt => dt.TextContent.Trim(), dt => dt.NextElementSibling!);
        Assert.Equal("PostgreSQL Server", values["Type"].TextContent.Trim());

        var graph = cut.FindComponent<ResourceRelationshipGraph>().Instance;
        Assert.Equal(new RelationshipGraphNode("Orders Database", "PostgreSQL Server", Url: null, ColorKey: "postgres"), graph.Resource);
        Assert.Equal([new RelationshipGraphNode("Orders API", "Reference", "/resources/api", ColorKey: "api")], graph.Incoming);
    }

    [Fact]
    public void Overview_MultipleSelection_LabeledResourcesStaySelectedAndInScope()
    {
        var cut = RenderOverview("/resources?resource=postgres&resource=api", resourceName: null);

        cut.WaitForAssertion(() => Assert.Equal(
            ["Orders API", "Orders Database"],
            cut.FindAll(".resource-card").Select(card => card.GetAttribute("aria-label")).Order(StringComparer.Ordinal)));

        Assert.Equal(
            ["Orders API", "Orders Database"],
            cut.FindAll(".resource-row.selected .resource-row-name").Select(name => name.TextContent.Trim()).Order(StringComparer.Ordinal));
        Assert.Equal(["postgres", "api"], cut.Instance.SelectedResourceNames);
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(string url, string? resourceName)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("postgres", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running,
                    resourceDisplayName: "Orders Database", resourceTypeDisplayName: "PostgreSQL Server"),
                ModelTestHelpers.CreateResource("api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running,
                    resourceDisplayName: "Orders API", relationships: [new RelationshipViewModel("postgres", "Reference")])
            ],
            resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), resourceName);
            builder.CloseComponent();
        }));
    }
}
