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
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewTypeDisplayNameTests : DashboardTestContext
{
    [Fact]
    public void Overview_DisplaysTypeLabelAndKeepsTechnicalTypeInProperties()
    {
        var cut = RenderOverview("postgres");

        cut.WaitForAssertion(() => Assert.Equal(["postgres"], cut.Instance.SelectedResourceNames));

        var status = cut.Find("[aria-labelledby='overview-status-title']");
        var values = status.QuerySelectorAll("dt").ToDictionary(dt => dt.TextContent.Trim(), dt => dt.NextElementSibling!);
        Assert.Equal("PostgreSQL Server", values["Type"].TextContent.Trim());

        var graph = cut.FindComponent<ResourceRelationshipGraph>().Instance;
        Assert.Equal(new RelationshipGraphNode("postgres", "PostgreSQL Server", Url: null, IsReplica: false), graph.Resource);

        Assert.Contains(KnownResourceTypes.Container, cut.Find(".overview-property-grid").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_WithoutTypeLabel_TechnicalTypeOnlyDisplaysInStatus()
    {
        var cut = RenderOverview("api");

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.Instance.SelectedResourceNames));

        var status = cut.Find("[aria-labelledby='overview-status-title']");
        var values = status.QuerySelectorAll("dt").ToDictionary(dt => dt.TextContent.Trim(), dt => dt.NextElementSibling!);
        Assert.Equal(KnownResourceTypes.Project, values["Type"].TextContent.Trim());

        Assert.Empty(cut.FindAll(".overview-property-grid"));
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(string resourceName)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("postgres", resourceType: KnownResourceTypes.Container, state: KnownResourceState.Running,
                    resourceTypeDisplayName: "PostgreSQL Server", properties: CreateTypeProperty(KnownResourceTypes.Container)),
                ModelTestHelpers.CreateResource("api", resourceType: KnownResourceTypes.Project, state: KnownResourceState.Running,
                    relationships: [new RelationshipViewModel("postgres", "Reference")], properties: CreateTypeProperty(KnownResourceTypes.Project))
            ],
            resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/resources/{resourceName}");

        return Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), resourceName);
            builder.CloseComponent();
        }));
    }

    private static Dictionary<string, ResourcePropertyViewModel> CreateTypeProperty(string resourceType) => new()
    {
        [KnownProperties.Resource.Type] = new(KnownProperties.Resource.Type, Value.ForString(resourceType), false, null, 0, null, false)
    };
}
