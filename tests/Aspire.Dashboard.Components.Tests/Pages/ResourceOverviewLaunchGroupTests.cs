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
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewLaunchGroupTests : DashboardTestContext
{
    private static readonly (string Name, string Value)[] s_expectedProperties =
    [
        ("Selected group", "Web"),
        ("Effective membership", "Web, Shared"),
        ("Inherited from", "parent"),
        ("Explicit start", "Yes"),
        ("Exclusion reason", "Not in selected group"),
        ("Build disposition", "Deferred")
    ];

    [Fact]
    public void Overview_DisplaysReadOnlyLaunchGroupMetadataSeparately()
    {
        var cut = RenderOverview(withLaunchGroups: true, customPropertyCount: 0);

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.Instance.SelectedResourceNames));
        var values = GetStatusValues(cut);

        Assert.Equal("Web", values["Selected group"]);
        Assert.Equal("Web, Shared", values["Effective membership"]);
        Assert.Equal("parent", values["Inherited from"]);
        Assert.Equal("Yes", values["Explicit start"]);
        Assert.Equal("Not in selected group", values["Exclusion reason"]);
        Assert.Equal("Deferred", values["Build disposition"]);
        Assert.Equal("Running", values["State"]);
    }

    [Fact]
    public void Overview_PropertiesDisplayReadOnlyLaunchGroupMetadata()
    {
        var cut = RenderOverview(withLaunchGroups: true, customPropertyCount: 0);

        cut.WaitForAssertion(() => Assert.Equal(s_expectedProperties, GetPropertyValues(cut)));
    }

    [Fact]
    public void Overview_ExpandedAndCorePropertiesIncludeLaunchGroupMetadata()
    {
        var cut = RenderOverview(withLaunchGroups: true, customPropertyCount: 11);
        var propertiesSelector = "[aria-labelledby='overview-properties-title']";

        cut.WaitForAssertion(() => Assert.Equal(10, GetPropertyValues(cut).Length));
        cut.Find($"{propertiesSelector} .overview-show-more").Click();

        cut.WaitForAssertion(() =>
        {
            var properties = GetPropertyValues(cut);
            Assert.Equal(17, properties.Length);
            Assert.Equal(s_expectedProperties, properties.Skip(11));
        });

        cut.Find($"{propertiesSelector} [aria-label='Show core properties']").Click();
        cut.WaitForAssertion(() => Assert.Equal(s_expectedProperties, GetPropertyValues(cut)));
    }

    [Theory]
    [InlineData(LaunchGroupBuildDisposition.Initial)]
    [InlineData(LaunchGroupBuildDisposition.InitialPersistent)]
    [InlineData(LaunchGroupBuildDisposition.InitialShared)]
    public void Overview_DefaultMetadataDoesNotShowExtraRows(LaunchGroupBuildDisposition buildDisposition)
    {
        var cut = RenderOverview(
            withLaunchGroups: true,
            customPropertyCount: 0,
            launchGroup: LaunchGroupTestHelpers.CreateResource(
                "api",
                groups: ["Web"],
                excludedFromSelection: true,
                buildDisposition: buildDisposition));

        cut.WaitForAssertion(() =>
        {
            var status = GetStatusValues(cut);
            Assert.Equal(["Selected group", "Effective membership"], status.Keys.TakeLast(2));
            Assert.Equal([("Selected group", "Web"), ("Effective membership", "Web")], GetPropertyValues(cut));
        });
    }

    [Fact]
    public void Overview_GroupValuesExposeEffectiveMembersOnHoverAndFocus()
    {
        var cut = RenderOverview(withLaunchGroups: true, customPropertyCount: 0);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("Web", "api, worker"), ("Web", "api, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-status-title']")));
            Assert.Equal(
                [("Web", "api, worker"), ("Web", "api, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-properties-title']")));
        });
    }

    [Fact]
    public async Task Overview_GroupMembersFollowHiddenResourceVisibility()
    {
        var cut = RenderOverview(withLaunchGroups: true, customPropertyCount: 0);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("Web", "api, worker"), ("Web", "api, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-status-title']")));
            Assert.Equal(
                [("Web", "api, worker"), ("Web", "api, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-properties-title']")));
        });

        cut.Find($"fluent-button[aria-label='{Resources.Resources.ResourcesChangeViewOptions}']").Click();
        var menu = cut.FindComponents<AspireMenu>().Single(m =>
            m.Instance.Items.Any(item => item.Text == Resources.ControlsStrings.ShowHiddenResources));
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Text == Resources.ControlsStrings.ShowHiddenResources).OnClick!);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("Web", "api, api-rebuilder, worker"), ("Web", "api, api-rebuilder, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-status-title']")));
            Assert.Equal(
                [("Web", "api, api-rebuilder, worker"), ("Web", "api, api-rebuilder, worker"), ("Shared", "api, cache")],
                GetGroupValues(cut.Find("[aria-labelledby='overview-properties-title']")));
        });
    }

    [Fact]
    public void Overview_OlderAppHostKeepsDefaultStatusRows()
    {
        var cut = RenderOverview(withLaunchGroups: false, customPropertyCount: 0);

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.Instance.SelectedResourceNames));

        Assert.Equal(["State", "Health state", "Type", "Start time", "Stop time"], GetStatusValues(cut).Keys);
        Assert.Empty(GetPropertyValues(cut));
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(
        bool withLaunchGroups,
        int customPropertyCount,
        LaunchGroupResourceInfo? launchGroup = null)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var properties = Enumerable.Range(1, customPropertyCount).ToDictionary(
            number => $"custom-{number:D2}",
            number => new ResourcePropertyViewModel($"custom-{number:D2}", Value.ForString($"value-{number}"), false, null, 0, null, false));
        var resource = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, properties: properties);
        var resources = new List<ResourceViewModel>
        {
            resource,
            ModelTestHelpers.CreateResource("api-rebuilder", hidden: true),
            ModelTestHelpers.CreateResource("worker-1", displayName: "worker", hidden: true),
            ModelTestHelpers.CreateResource("worker-2", displayName: "worker")
        };
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        IDashboardClient client = withLaunchGroups
            ? new LaunchGroupTestDashboardClient(
                LaunchGroupTestHelpers.CreateInformation(
                    selectedGroup: "Web",
                    groups: ["Web", "Shared"],
                    resources:
                    [
                        launchGroup ?? LaunchGroupTestHelpers.CreateResource(
                            "api",
                            groups: ["Web", "Shared"],
                            inheritedFrom: "parent",
                            explicitStart: true,
                            excludedByGroup: true,
                            buildDisposition: LaunchGroupBuildDisposition.Deferred),
                        LaunchGroupTestHelpers.CreateResource("worker", groups: ["Web"], inheritedFrom: "api"),
                        LaunchGroupTestHelpers.CreateResource("api-rebuilder", groups: ["Web"], inheritedFrom: "api"),
                        LaunchGroupTestHelpers.CreateResource("cache", groups: ["Shared"])
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

    private static (string Name, string Value)[] GetPropertyValues(IRenderedComponent<ResourcesLayout> cut)
    {
        var properties = cut.Find("[aria-labelledby='overview-properties-title']");
        return properties.QuerySelectorAll(".fluent-data-grid-row")
            .Select(row => (
                row.QuerySelector(".nameColumn")!.TextContent.Trim(),
                row.QuerySelector(".valueColumn")!.TextContent.Trim()))
            .ToArray();
    }

    private static (string Group, string Members)[] GetGroupValues(AngleSharp.Dom.IElement container)
    {
        return container.QuerySelectorAll(".launch-group-value")
            .Select(element =>
            {
                var members = element.GetAttribute("data-tooltip");
                Assert.Equal("0", element.GetAttribute("tabindex"));
                Assert.Equal(members, element.GetAttribute("aria-description"));
                return (element.TextContent.Trim(), members!);
            })
            .ToArray();
    }
}
