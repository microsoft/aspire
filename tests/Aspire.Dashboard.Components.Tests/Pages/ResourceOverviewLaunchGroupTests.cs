// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewLaunchGroupTests : DashboardTestContext
{
    [Fact]
    public void Overview_DisplaysReadOnlyLaunchGroupMetadataSeparately()
    {
        var cut = RenderOverview(withLaunchGroups: true);

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.Instance.SelectedResourceNames));
        var values = GetStatusValues(cut);

        Assert.Equal("Web", values["Selected group"]);
        Assert.Equal("Web, Shared", values["Effective membership"]);
        Assert.Equal("parent", values["Inherited from"]);
        Assert.Equal("Yes", values["Explicit start"]);
        Assert.Equal("Yes", values["Excluded by selected group"]);
        Assert.Equal("No", values["Excluded from selection"]);
        Assert.Equal("Deferred", values["Build disposition"]);
        Assert.Equal("Running", values["State"]);
    }

    [Fact]
    public void Overview_OlderAppHostKeepsDefaultStatusRows()
    {
        var cut = RenderOverview(withLaunchGroups: false);

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.Instance.SelectedResourceNames));

        Assert.Equal(["State", "Health state", "Type", "Start time", "Stop time"], GetStatusValues(cut).Keys);
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(bool withLaunchGroups)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resource = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running);
        var resources = new List<ResourceViewModel> { resource };
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        IDashboardClient client = withLaunchGroups
            ? new LaunchGroupTestDashboardClient(
                LaunchGroupTestHelpers.CreateInformation(
                    selectedGroup: "Web",
                    groups: ["Web", "Shared"],
                    resources:
                    [
                        LaunchGroupTestHelpers.CreateResource(
                            "api",
                            groups: ["Web", "Shared"],
                            inheritedFrom: "parent",
                            explicitStart: true,
                            excludedByGroup: true,
                            buildDisposition: LaunchGroupBuildDisposition.Deferred)
                    ]),
                resources,
                () => channel)
            : new TestDashboardClient(isEnabled: true, initialResources: resources, resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");

        return Render<ResourcesLayout>(parameters => parameters
            .AddCascadingValue(viewport)
            .Add(layout => layout.Body, builder =>
            {
                builder.OpenComponent<ResourceOverview>(0);
                builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "api");
                builder.CloseComponent();
            }));
    }

    private static Dictionary<string, string> GetStatusValues(IRenderedComponent<ResourcesLayout> cut)
    {
        var status = cut.Find("[aria-labelledby='overview-status-title']");
        return status.QuerySelectorAll("dt").ToDictionary(
            term => term.TextContent.Trim(),
            term => term.NextElementSibling!.TextContent.Trim());
    }
}
