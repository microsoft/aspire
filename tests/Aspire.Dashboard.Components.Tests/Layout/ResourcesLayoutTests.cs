// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutTests : DashboardTestContext
{
    [Fact]
    public void AppHostTagsParticipateInGroupingWithoutUserTags()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.DashboardTag;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", properties: new()
                {
                    [name] = new(name, Value.ForList(Value.ForString("Backend"), Value.ForString("API")), false, null, 0, null, false)
                }),
                ModelTestHelpers.CreateResource("cache")
            ], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "Tags") : (false, null);
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal(["Tags:API", "Tags:Backend", "Tags:"],
            cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key"))));
        Assert.Equal(["api", "api", "cache"], cut.FindAll(".resource-tag-group .resource-row-name").Select(n => n.TextContent.Trim()));
        Assert.Empty(Services.GetRequiredService<ResourceTagStore>().GetAllTags());
    }

    [Theory]
    [InlineData("Tags")]
    [InlineData("ResourceType")]
    public void Grouping_ParametersJoinTagGroupsAndKeepTheirSectionOtherwise(string grouping)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.DashboardTag;
        Dictionary<string, ResourcePropertyViewModel> Tags(params string[] tags) => new()
        {
            [name] = new(name, Value.ForList([.. tags.Select(Value.ForString)]), false, null, 0, null, false)
        };
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", resourceType: "Project", properties: Tags("Backend")),
                ModelTestHelpers.CreateResource("api-key", resourceType: KnownResourceTypes.Parameter, properties: Tags("Backend")),
                ModelTestHelpers.CreateResource("theme", resourceType: KnownResourceTypes.Parameter)
            ], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, grouping) : (false, null);
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".resource-tag-group")));
        var groups = cut.FindAll(".resource-tag-group").Select(g => (
            g.GetAttribute("data-resource-group-key"),
            string.Join(", ", g.QuerySelectorAll(".resource-row-name").Select(n => n.TextContent.Trim())))).ToList();
        var sections = cut.FindAll(".resource-pane-section-header > span:first-child").Select(s => s.TextContent.Trim()).ToList();
        if (grouping == "Tags")
        {
            // A parameter shows with the resources that share its tags, after them. Untagged ones are with the untagged.
            Assert.Equal([("Tags:Backend", "api, api-key"), ("Tags:", "theme")], groups);
            Assert.Empty(sections);
        }
        else
        {
            // Other groupings are about resources: the parameters stay in their own section rather than disappearing.
            Assert.Equal([("ResourceType:Project", "api")], groups);
            Assert.Equal(["Parameters"], sections);
            Assert.Equal(
                ["api-key", "theme"],
                cut.FindAll(".resource-pane-section-header ~ .resource-row .resource-row-name").Select(n => n.TextContent.Trim()));
        }
    }

    [Theory]
    [InlineData("Tags", "Tags:A new group")]
    [InlineData("ResourceType", "ResourceType:AAA")]
    [InlineData("State", "State:Building")]
    public async Task AppHost_GroupMovesPreserveMembershipAndModeSpecificOrderAndAppendNewGroups(string grouping, string newGroupKey)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Exited),
                ModelTestHelpers.CreateResource("worker", resourceType: "Executable")
            ], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, grouping) : (false, null);
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api", "Alpha");
        tags.AddTag("cache", "Beta");
        tags.AddTag("api", "Untagged");
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var store = Services.GetRequiredService<ResourceOrderStore>();
        var resourcePositions = store.GetPositions();
        var otherModes = new[] { "Tags", "ResourceType", "State" }.Where(mode => mode != grouping)
            .ToDictionary(mode => mode, store.GetGroupPositions);
        var originalKeys = GetKeys();
        var memberships = cut.FindAll(".resource-tag-group").ToDictionary(group => group.GetAttribute("data-resource-group-key")!,
            group => group.QuerySelectorAll(".resource-row-name").Select(name => name.TextContent.Trim()).ToArray());
        var movedKey = originalKeys[^1];
        var targetKey = originalKeys[0];
        Assert.Equal(originalKeys.Length, cut.FindAll("[data-resource-group-drag-handle]").Count);
        Assert.Equal("true", cut.Find(".resource-pane-list").GetAttribute("data-group-reorder-enabled"));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync(grouping, movedKey, targetKey, after: false)));
        var expectedKeys = new[] { movedKey }.Concat(originalKeys[..^1]).ToArray();
        Assert.Equal(expectedKeys, GetKeys());
        Assert.All(memberships, pair => Assert.Equal(pair.Value,
            GetGroup(pair.Key).QuerySelectorAll(".resource-row-name").Select(name => name.TextContent.Trim())));
        Assert.Equal(resourcePositions, store.GetPositions());
        Assert.All(otherModes, pair => Assert.Equal(pair.Value, store.GetGroupPositions(pair.Key)));
        Assert.Equal(["Alpha", "Untagged"], tags.GetTags("api"));
        Assert.Equal(["Beta"], tags.GetTags("cache"));

        GetGroup(movedKey).QuerySelector(".resource-row-toggle")!.Click();
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync(grouping, movedKey, targetKey, after: true)));
        Assert.Empty(GetGroup(movedKey).QuerySelectorAll(".resource-row"));
        Assert.Equal("false", GetGroup(movedKey).QuerySelector(".resource-row-toggle")!.GetAttribute("aria-expanded"));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync(grouping, movedKey, targetKey, after: false)));
        GetGroup(movedKey).QuerySelector(".resource-row-toggle")!.Click();

        var search = cut.FindComponent<FluentTextInput>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("api"));
        if (grouping == "Tags")
        {
            tags.AddTag("api", "A new group");
        }
        else
        {
            await channel.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert,
                ModelTestHelpers.CreateResource("new-resource", state: KnownResourceState.Building, resourceType: "AAA"))]);
        }
        cut.WaitForAssertion(() => Assert.True(store.GetGroupPositions(grouping).ContainsKey(newGroupKey[(grouping.Length + 1)..])));
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync(string.Empty));
        cut.WaitForAssertion(() => Assert.Equal(expectedKeys.Append(newGroupKey), GetKeys()));

        cut.Find("#resourcePaneGroupingButton").Click();
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.MenuButtonId == "resourcePaneGroupingButton");
        var otherMode = otherModes.Keys.First();
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == $"resource-grouping-{otherMode}").OnClick!);
        Assert.False(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync(grouping, movedKey, targetKey, after: false)));
        cut.Find("#resourcePaneGroupingButton").Click();
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == $"resource-grouping-{grouping}").OnClick!);
        Assert.Equal(expectedKeys.Append(newGroupKey), GetKeys());

        string[] GetKeys() => cut.FindAll(".resource-tag-group").Select(group => group.GetAttribute("data-resource-group-key")!).ToArray();
        AngleSharp.Dom.IElement GetGroup(string key) => cut.FindAll(".resource-tag-group").Single(group => group.GetAttribute("data-resource-group-key") == key);
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("types")]
    [InlineData("states")]
    [InlineData("health")]
    public async Task AppHost_GroupReorderingRequiresClearSearchAndFilters(string filter)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Exited)
            ], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "State") : (false, null);
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-group-drag-handle]").Count));
        var positions = Services.GetRequiredService<ResourceOrderStore>().GetGroupPositions("State");
        Func<Task> reset;
        if (filter == "Search")
        {
            var search = cut.FindComponent<FluentTextInput>();
            await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("a"));
            reset = () => search.Instance.ValueChanged.InvokeAsync(string.Empty);
        }
        else
        {
            var filterPopover = OpenFilter(cut);
            await SetAllFilterValuesAsync(cut, filterPopover, filter, isVisible: false);
            reset = () => SetAllFilterValuesAsync(cut, filterPopover, filter, isVisible: true);
        }

        Assert.Equal("false", cut.Find(".resource-pane-list").GetAttribute("data-group-reorder-enabled"));
        Assert.Empty(cut.FindAll("[data-resource-group-drag-handle]"));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync("State", "State:Running", "State:Exited", after: false)));
        Assert.Equal(positions, Services.GetRequiredService<ResourceOrderStore>().GetGroupPositions("State"));
        await cut.InvokeAsync(reset);
        Assert.Equal(2, cut.FindAll("[data-resource-group-drag-handle]").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppHost_ResourceLinksHaveAccessibleMetadataInsteadOfHoverTooltips(bool collapsed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, properties: new()
            {
                [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString("Persistent"), false, null, 0, null, false)
            })], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, collapsed) : (false, null);
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Equal(
            [
                ("cache", "Running; Container (persistent)", (string?)null)
            ],
            cut.FindAll(".resource-row-link").Select(link => (
                link.GetAttribute("aria-label"), link.GetAttribute("aria-description"), link.GetAttribute("data-tooltip"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standalone_SourceLinksHaveAccessibleNamesInsteadOfHoverTooltips(bool collapsed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, collapsed) : (false, null);
        await AddTelemetryAsync("api");
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Equal(
            [
                ("All sources", "1 telemetry source", (string?)null),
                ("api", Resources.Layout.ResourcePaneTelemetryOnly, null)
            ],
            cut.FindAll(".resource-row-link").Select(link => (
                link.GetAttribute("aria-label"), link.GetAttribute("aria-description"), link.GetAttribute("data-tooltip"))));
    }

    [Fact]
    public async Task AppHost_TelemetryOnlyResourcesAreHiddenUntilEnabled()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        await AddTelemetryAsync("browser");

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Equal(["cache"], cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("aria-label")));

        var telemetryToggle = cut.Find(".resource-kind-toggle[aria-label='Telemetry sources']");
        Assert.Equal("false", telemetryToggle.GetAttribute("aria-pressed"));
        Assert.Equal("1 telemetry source", telemetryToggle.GetAttribute("aria-description"));

        telemetryToggle.Click();

        cut.WaitForAssertion(() => Assert.Equal(
            ["cache", "browser"],
            cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("aria-label"))));
        Assert.Equal("1 resource, 1 telemetry source", cut.Find(".resource-pane-foot > span").GetAttribute("aria-label"));
    }

    [Fact]
    public void AppHost_CountsResourcesAndParametersSeparately()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("worker"),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter)
            ],
            resourceChannelProvider: () => channel));

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                [("Resources", "2 resources"), ("Parameters", "1 parameter"), ("Telemetry sources", "0 telemetry sources")],
                cut.FindAll(".resource-kind-toggle").Select(toggle => (toggle.GetAttribute("aria-label")!, toggle.GetAttribute("aria-description")!)));
            var countSummary = cut.Find(".resource-pane-foot > span");
            Assert.Equal("img", countSummary.GetAttribute("role"));
            Assert.Equal("2 resources, 1 parameter", countSummary.GetAttribute("aria-label"));
        });
    }

    [Fact]
    public async Task AppHost_ReorderingPreservesHierarchyAndAppendsNewSiblings()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("root"),
                ModelTestHelpers.CreateResource("worker"),
                ModelTestHelpers.CreateResource("a", properties: new()
                {
                    [KnownProperties.Resource.ParentName] = new(KnownProperties.Resource.ParentName, Value.ForString("root"), false, null, 0, null, false)
                }),
                ModelTestHelpers.CreateResource("b", properties: new()
                {
                    [KnownProperties.Resource.ParentName] = new(KnownProperties.Resource.ParentName, Value.ForString("root"), false, null, 0, null, false)
                })
            ], resourceChannelProvider: () => channel));
        var store = Services.GetRequiredService<ResourceOrderStore>();
        store.EnsureResources(["worker_0", "root_0", "b_0", "a_0"]);
        var toasts = Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(["worker", "root", "b", "a"], GetNames()));

        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("root", "worker", after: false)));
        Assert.Equal(["root", "b", "a", "worker"], GetNames());
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("a", "b", after: false)));
        Assert.Equal(["root", "a", "b", "worker"], GetNames());
        Assert.False(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("a", "worker", after: true)));
        Assert.Equal(Resources.Layout.ResourcePaneReorderUnavailable, Assert.Single(toasts.FindComponents<FluentToast>()).Instance.Title);
        Assert.Equal(["root", "a", "b", "worker"], GetNames());

        cut.Find("[data-resource-name='root'] .resource-row-toggle").Click();
        Assert.Equal(["root", "worker"], GetNames());
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("worker", "root", after: false)));
        cut.Find("[data-resource-name='root'] .resource-row-toggle").Click();
        Assert.Equal(["worker", "root", "a", "b"], GetNames());

        await channel.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("0", properties: new()
        {
            [KnownProperties.Resource.ParentName] = new(KnownProperties.Resource.ParentName, Value.ForString("root"), false, null, 0, null, false)
        }))]);
        cut.WaitForAssertion(() => Assert.Equal(["worker", "root", "a", "b", "0"], GetNames()));

        Assert.Equal(["worker_0", "root_0", "a_0", "b_0", "0_0"],
            store.GetPositions().OrderBy(pair => pair.Value).Select(pair => pair.Key));

        string?[] GetNames() => cut.FindAll("[data-resource-name]").Select(row => row.GetAttribute("data-resource-name")).ToArray();
    }

    [Fact]
    public async Task CompactDensity_RendersOneLineRowsWithAlignedColumns()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, resourceType: "Container", properties: new()
                {
                    [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString("Persistent"), false, null, 0, null, false)
                })
            ], resourceChannelProvider: () => channel));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name] .resource-row-sub").Count));
        Assert.Empty(cut.FindAll(".resource-row-meta"));

        // The setting applies right away, without reloading the page.
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Compact));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-pane-list.density-compact")));
        Assert.Empty(cut.FindAll("[data-resource-name] .resource-row-sub"));
        Assert.Empty(cut.FindAll("[data-resource-name='api'] .persistent-container-icon"));
        var api = cut.Find("[data-resource-name='api'] .resource-row-meta");
        Assert.Equal("Project", api.QuerySelector(".resource-row-type")!.TextContent);
        Assert.Equal("Running", api.QuerySelector(".resource-row-meta-state")!.GetAttribute("data-tooltip"));
        Assert.Empty(cut.FindAll("[data-resource-name='cache'] .persistent-container-icon"));
        var cache = cut.Find("[data-resource-name='cache'] .resource-row-meta");
        Assert.Empty(cache.QuerySelectorAll(".persistent-container-icon"));
        Assert.Equal("Container (persistent)", cache.QuerySelector(".resource-row-type")!.TextContent);
    }

    [Fact]
    public async Task TerminalTab_NavigatesWithinTheSelectionAndFallsBackForNonTerminalResources()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                TerminalSetupHelpers.CreateTerminalResource("shell"),
                TerminalSetupHelpers.CreateTerminalResource("other-shell"),
                ModelTestHelpers.CreateResource("api")
            ], resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.ResourceOverviewUrl("shell"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.True(cut.Instance.IsTabAvailable(ResourceTab.Terminal));
        var terminalTab = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Terminal");
        Assert.True(terminalTab.Instance.Visible);
        await cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(terminalTab.Instance));
        Assert.Equal("http://localhost/resources/shell?terminal=true", navigation.Uri);
        cut.Render();
        Assert.Equal("tab-Terminal", cut.FindComponent<FluentTabs>().Instance.ActiveTabId);
        var previousTabs = cut.FindComponent<FluentTabs>().Instance;
        var previousTabChanged = cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged;
        var overviewTab = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Overview").Instance;

        cut.Find(".resource-row-link[aria-label='other-shell']").Click();
        Assert.Equal("http://localhost/resources/other-shell?terminal=true", navigation.Uri);
        cut.Render();
        Assert.NotSame(previousTabs, cut.FindComponent<FluentTabs>().Instance);
        Assert.Equal(["other-shell"], cut.Instance.SelectedResourceNames);
        await cut.InvokeAsync(() => previousTabChanged.InvokeAsync(overviewTab));
        Assert.Equal("http://localhost/resources/other-shell?terminal=true", navigation.Uri);

        cut.Find(".resource-row-link[aria-label='api']").Click();
        Assert.Equal("http://localhost/resources/api", navigation.Uri);
        cut.Render();
        Assert.False(cut.Instance.IsTabAvailable(ResourceTab.Terminal));
        Assert.False(cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Terminal").Instance.Visible);
    }

    [Fact]
    public async Task TerminalTab_FocusAndOverviewPreservePaneQueryAndFragment()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/shell?pane=tags#output");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var terminal = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Terminal").Instance;
        await cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(terminal));
        Assert.Equal("http://localhost/resources/shell?pane=tags&terminal=true#output", navigation.Uri);
        cut.Render();
        var overview = cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Overview").Instance;
        await cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(overview));
        Assert.Equal("http://localhost/resources/shell?pane=tags#output", navigation.Uri);
    }

    [Fact]
    public void Tabs_ShowTheSelectionsPagesAndNeverDisableThoseWithoutTelemetry()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("worker")],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.AddResourceSelection(DashboardUrls.ConsoleLogsUrl(), ["api", "worker"]));

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal(
            ["tab-Overview", "tab-Console", "tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
            cut.FindComponents<FluentTab>().Where(t => t.Instance.Visible).Select(t => t.Instance.Id)));
        Assert.Equal("tab-Console", cut.FindComponent<FluentTabs>().Instance.ActiveTabId);
        // The resources send no telemetry, so their telemetry tabs are flagged unavailable but stay enabled.
        Assert.DoesNotContain(cut.FindComponents<FluentTab>(), t => t.Instance.Disabled);
        Assert.Equal(
            ["tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
            cut.FindComponents<FluentTab>().Where(t => t.Markup.Contains(Resources.Layout.ResourceTabUnavailable)).Select(t => t.Instance.Id));

        // Selecting an unavailable tab still navigates.
        cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(
            cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Traces").Instance));
        Assert.Equal("http://localhost/traces?resource=api&resource=worker", navigation.Uri);
        cut.Render();

        cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(
            cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Overview").Instance));
        Assert.Equal("http://localhost/resources?resource=api&resource=worker", navigation.Uri);
    }

    [Fact]
    public async Task Tabs_ClickedTabStaysActiveUntilItsPageShows()
    {
        // The tab strip is inline on narrow viewports; on desktop it's in the app's header, which isn't rendered here.
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.ResourceOverviewUrl("api"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal("tab-Overview", cut.FindComponent<FluentTabs>().Instance.ActiveTabId));

        Task SelectTabAsync(string id) => cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(
            cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == id).Instance));

        await SelectTabAsync("tab-Console");

        // Until the router gives the layout the new page, the strip keeps the clicked tab. Rendering the previous tab
        // would make the strip report it as a change once the new page shows, which navigated back, endlessly.
        Assert.Equal("http://localhost/consolelogs/resource/api", navigation.Uri);
        Assert.Equal("tab-Console", cut.FindComponent<FluentTabs>().Instance.ActiveTabId);
        await SelectTabAsync("tab-Overview");
        Assert.Equal("http://localhost/consolelogs/resource/api", navigation.Uri);

        // Once the new page shows (the router sets the layout's parameters again), tab clicks navigate again.
        cut.Render();
        await SelectTabAsync("tab-Overview");
        Assert.Equal("http://localhost/resources/api", navigation.Uri);
    }

    [Fact]
    public async Task RowActions_RunHighlightedCommandsAndOfferTheResourceMenu()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var executed = new List<(string Resource, string Command)>();
        var stop = new CommandViewModel("resource-stop", CommandViewModelState.Enabled, "Stop", "Stop resource", confirmationMessage: "", argumentInputs: [], isHighlighted: true, iconName: "Stop", iconVariant: IconVariant.Filled);
        var other = new CommandViewModel("custom", CommandViewModelState.Enabled, "Custom command", "", confirmationMessage: "", argumentInputs: [], isHighlighted: false, iconName: string.Empty, iconVariant: IconVariant.Regular);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, commands: [stop, other])],
            resourceChannelProvider: () => channel,
            executeResourceCommand: (resource, _, command, _, _) =>
            {
                executed.Add((resource, command.Name));
                return Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Succeeded });
            }));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ConsoleLogsUrl("api"));
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-resource-name='api'] .resource-row-actions")));

        // The actions are on the rows and in the overview, not above the resource's other pages.
        Assert.Empty(cut.FindAll(".resource-header .resource-actions"));
        var stopButton = Assert.Single(cut.FindAll("[data-resource-name='api'] .resource-row-actions fluent-button[aria-label='Stop api']"));
        await cut.InvokeAsync(() => stopButton.Click());
        cut.WaitForAssertion(() => Assert.Equal([("api", "resource-stop")], executed));

        var menuButton = cut.FindComponents<AspireMenuButton>().Single(b => b.Instance.Title == "Actions for api");
        Assert.Contains(menuButton.Instance.ItemsProvider(), item => item.Text == "Custom command");
    }

    [Theory]
    [InlineData(true, ResourceListDensity.Comfortable)]
    [InlineData(true, ResourceListDensity.Compact)]
    [InlineData(true, ResourceListDensity.Spacious)]
    [InlineData(false, ResourceListDensity.Comfortable)]
    public async Task RowTerminal_SelectsOnlyItsResourceAndPreservesViewState(bool desktop, ResourceListDensity density)
    {
        var viewport = new ViewportInformation(IsDesktop: desktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var stop = new CommandViewModel("resource-stop", CommandViewModelState.Enabled, "Stop", "Stop resource", confirmationMessage: "", argumentInputs: [], isHighlighted: true, iconName: "Stop", iconVariant: IconVariant.Filled);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("shell-instance", displayName: "shell", state: KnownResourceState.Running,
                    urls: [new UrlViewModel("https", new Uri("https://localhost:5001"), false, false, UrlDisplayPropertiesViewModel.Empty)],
                    commands: [stop],
                    properties: new()
                    {
                        [KnownProperties.Terminal.Enabled] = new(KnownProperties.Terminal.Enabled, Value.ForString("true"), false, null, 0, null, false)
                    }),
                ModelTestHelpers.CreateResource("api")
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        const string initialUrl = "/resources?resource=api&resource=shell&pane=tags&filter=one&filter=two#output";
        const string terminalUrl = "/resources/shell?pane=tags&filter=one&filter=two&terminal=true#output";
        navigation.NavigateTo(initialUrl);
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(density));

        var terminal = Assert.Single(cut.FindAll(".resource-row-terminal"));
        Assert.Equal(terminalUrl, terminal.GetAttribute("href"));
        Assert.Equal("Open terminal for shell", terminal.GetAttribute("aria-label"));
        Assert.Equal("Open terminal for shell", terminal.GetAttribute("data-tooltip"));
        Assert.Collection(terminal.ParentElement!.Children,
            endpoint => Assert.True(endpoint.ClassList.Contains("resource-row-action-priority-0")),
            shortcut => Assert.True(shortcut.ClassList.Contains("resource-row-action-priority-1")),
            command => Assert.True(command.ClassList.Contains("resource-row-action-priority-2")),
            menu => Assert.True(menu.ClassList.Contains("aspire-menu-button")));

        if (!desktop)
        {
            cut.Find(".resource-pane-open-button").Click();
            Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        }

        // Modifier clicks on the shortcut must not invoke the row's multi-selection behavior.
        await cut.InvokeAsync(() => cut.Find(".resource-row-terminal").Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { CtrlKey = true }));
        Assert.Equal("http://localhost" + terminalUrl, navigation.Uri);
        cut.Render();
        Assert.Equal(["shell"], cut.Instance.SelectedResourceNames);
        Assert.Equal(ResourceTab.Terminal, cut.Instance.CurrentTab);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));

        // The narrow-pane overflow menu must have the same navigation semantics as the shortcut.
        navigation.NavigateTo(initialUrl);
        cut.Render();
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.Title == "Actions for shell");
        var terminalItem = Assert.Single(menu.Instance.ItemsProvider(), item => item.Text == "Terminal");
        await cut.InvokeAsync(Assert.IsType<Func<Task>>(terminalItem.OnClick));
        Assert.Equal("http://localhost" + terminalUrl, navigation.Uri);
        cut.Render();
        Assert.Equal(["shell"], cut.Instance.SelectedResourceNames);
        Assert.Equal(ResourceTab.Terminal, cut.Instance.CurrentTab);
    }

    [Fact]
    public void RowTerminal_IsUnavailableInReadOnlyDashboards()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true, isReadOnly: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Empty(cut.FindAll(".resource-row-terminal"));
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.Title == "Actions for shell");
        Assert.All(menu.Instance.ItemsProvider(), item => Assert.NotEqual("Terminal", item.Text));
        Assert.False(cut.Instance.IsTabAvailable(ResourceTab.Terminal));
    }

    // The filters are a popover that stays open while values change. Expands only the given group, so the checkboxes
    // found are that group's: the first is "(All)", the rest are its values in order.
    private static IRenderedComponent<ResourcePaneFilter> OpenFilter(IRenderedComponent<ResourcesLayout> cut)
    {
        cut.Find("#resourcePaneFilterButton").Click();
        return cut.FindComponent<ResourcePaneFilter>();
    }

    private static List<IRenderedComponent<FluentCheckbox>> GetFilterCheckboxes(IRenderedComponent<ResourcePaneFilter> filter, string groupId)
    {
        var target = groupId switch { "types" => 0, "states" => 1, "health" => 2, _ => 3 };
        var count = filter.FindAll(".resource-filter-group-header").Count;
        for (var i = 0; i < count; i++)
        {
            var header = filter.FindAll(".resource-filter-group-header")[i];
            if ((header.GetAttribute("aria-expanded") == "true") != (i == target))
            {
                header.Click();
            }
        }

        return filter.FindComponents<FluentCheckbox>().ToList();
    }

    private static Task SetAllFilterValuesAsync(IRenderedComponent<ResourcesLayout> cut, IRenderedComponent<ResourcePaneFilter> filter, string groupId, bool isVisible) =>
        cut.InvokeAsync(() => GetFilterCheckboxes(filter, groupId)[0].Instance.CheckStateChanged.InvokeAsync(isVisible));

    [Fact]
    public void FilterPopover_ShowsHowManyResourcesEachValueMatches()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("worker", state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.FailedToStart)
            ],
            resourceChannelProvider: () => channel));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-resource-name]").Count));

        var filter = OpenFilter(cut);
        var boxes = GetFilterCheckboxes(filter, "states");

        // "All" has no count: it isn't a value of the filter.
        Assert.Equal("(All)", boxes[0].Instance.Label);
        Assert.Equal(
            [(nameof(KnownResourceState.FailedToStart), "1"), (nameof(KnownResourceState.Running), "2")],
            boxes.Skip(1).Select(box => box.Instance.Label!).Zip(filter.FindAll(".resource-filter-option .resource-filter-count").Select(count => count.TextContent.Trim())));
    }

    [Fact]
    public async Task FilterPopover_FiltersResourcesByTags()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("worker"),
                ModelTestHelpers.CreateResource("cache")
            ],
            resourceChannelProvider: () => channel));
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api", "Backend");
        tags.AddTag("worker", "Backend");
        tags.AddTag("worker", "Shared");

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-resource-name]").Count));

        var filter = OpenFilter(cut);
        var boxes = GetFilterCheckboxes(filter, "tags");

        Assert.Equal(
            [("(All)", (string?)null), ("Untagged", "1"), ("Backend", "2"), ("Shared", "1")],
            boxes.Select((box, index) => (box.Instance.Label!, index == 0 ? null : filter.FindAll(".resource-filter-option .resource-filter-count")[index - 1].TextContent.Trim())));

        await SetAllFilterValuesAsync(cut, filter, "tags", isVisible: false);
        await cut.InvokeAsync(() => GetFilterCheckboxes(filter, "tags").Single(box => box.Instance.Label == "Backend").Instance.ValueChanged.InvokeAsync(true));

        cut.WaitForAssertion(() => Assert.Equal(
            ["api", "worker"],
            cut.FindAll("[data-resource-name]").Select(e => e.GetAttribute("data-resource-name"))));

        // The popover stays open while values change, and OK only closes it.
        Assert.Single(cut.FindAll(".resource-filter"));
        cut.Find(".resource-filter-done").Click();
        Assert.Empty(cut.FindAll(".resource-filter"));
        Assert.Equal(["api", "worker"], cut.FindAll("[data-resource-name]").Select(e => e.GetAttribute("data-resource-name")));
    }

    [Fact]
    public async Task FilterPopover_TagGroupingShowsOnlyTheFilteredTags()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("worker"),
                ModelTestHelpers.CreateResource("cache")
            ],
            resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "Tags") : (false, null);
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api", "Backend");
        tags.AddTag("api", "API");
        tags.AddTag("worker", "Backend");

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(["Tags:API", "Tags:Backend", "Tags:"],
            cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key"))));

        var filter = OpenFilter(cut);
        await SetAllFilterValuesAsync(cut, filter, "tags", isVisible: false);
        await cut.InvokeAsync(() => GetFilterCheckboxes(filter, "tags").Single(box => box.Instance.Label == "Backend").Instance.ValueChanged.InvokeAsync(true));

        // api also carries API, but only the filtered tag keeps a group.
        cut.WaitForAssertion(() => Assert.Equal(["Tags:Backend"],
            cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key"))));
        Assert.Equal(["api", "worker"], cut.FindAll(".resource-tag-group .resource-row-name").Select(n => n.TextContent.Trim()));
    }

    [Fact]
    public async Task FilterPopover_ResetShowsEveryValueAgain()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Exited)
            ],
            resourceChannelProvider: () => channel));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name]").Count));

        var filter = OpenFilter(cut);
        Assert.True(cut.Find(".resource-filter-reset").HasAttribute("disabled"));

        await SetAllFilterValuesAsync(cut, filter, "states", isVisible: false);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-resource-name]")));
        Assert.False(cut.Find(".resource-filter-reset").HasAttribute("disabled"));

        cut.Find(".resource-filter-reset").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name]").Count));
        Assert.True(cut.Find(".resource-filter-reset").HasAttribute("disabled"));
    }

    [Fact]
    public async Task KindToggles_HideAndShowEachKindOfItem()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter)
            ],
            resourceChannelProvider: () => channel));
        await AddTelemetryAsync("api");
        await AddTelemetryAsync("browser");
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name]").Count));

        // Resources and parameters are on, telemetry sources are off. Each toggle names its kind and count.
        Assert.Equal(
            [("Resources", "1 resource", "true"), ("Parameters", "1 parameter", "true"), ("Telemetry sources", "1 telemetry source", "false")],
            cut.FindAll(".resource-kind-toggle").Select(toggle => (toggle.GetAttribute("aria-label")!, toggle.GetAttribute("aria-description")!, toggle.GetAttribute("aria-pressed")!)));
        Assert.Equal("All resources", cut.Find(".resource-header-title").TextContent.Trim());

        cut.Find(".resource-kind-toggle[aria-label='Parameters']").Click();
        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.FindAll("[data-resource-name]").Select(e => e.GetAttribute("data-resource-name"))));
        Assert.Equal("false", cut.Find(".resource-kind-toggle[aria-label='Parameters']").GetAttribute("aria-pressed"));
        Assert.Equal(["api"], cut.Instance.GetScopeResourceNames());

        cut.Find(".resource-kind-toggle[aria-label='Telemetry sources']").Click();
        cut.WaitForAssertion(() => Assert.Equal(["api", "browser"], cut.FindAll(".resource-row-name").Select(e => e.TextContent.Trim())));
        Assert.Equal(["Resources, Telemetry sources"], cut.FindAll(".resource-header-title").Select(e => e.TextContent.Trim()));
        Assert.Equal(["api", "browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        cut.Find(".resource-kind-toggle[aria-label='Resources']").Click();
        cut.WaitForAssertion(() => Assert.Equal(["browser"], cut.FindAll(".resource-row-name").Select(e => e.TextContent.Trim())));
        Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        cut.Find(".resource-kind-toggle[aria-label='Telemetry sources']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".resource-row-name")));
        Assert.Equal("Nothing shown", cut.Find(".resource-header-title").TextContent.Trim());
        Assert.Contains(Resources.Layout.ResourcePaneNoKindsHint, cut.Find(".resource-pane-message").TextContent);
    }

    [Fact]
    public void Landing_WithoutRememberedSelection_KeepsAllResourcesSelected()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourceListUrl());

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name]").Count));
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        // Nothing is selected, which means all resources: "Select all" is pressed.
        Assert.Equal("true", cut.Find(".resource-select-all-toggle").GetAttribute("aria-pressed"));
        Assert.Equal("All resources", cut.Find(".resource-header-title").TextContent.Trim());
    }

    [Theory]
    [InlineData("/resources/api")]
    [InlineData("/resources/api?terminal=true")]
    [InlineData("/terminals/resource/api")]
    public async Task SelectAll_IsPressedOnlyWhileEverythingIsSelectedAndSelectsEverything(string initialUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(initialUrl);
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal("api", cut.Find(".resource-header-title").TextContent.Trim()));
        Assert.Equal(["api"], cut.FindAll(".resource-row.selected[data-resource-name]").Select(r => r.GetAttribute("data-resource-name")));
        var selectAll = cut.Find(".resource-select-all-toggle");
        Assert.Equal("false", selectAll.GetAttribute("aria-pressed"));
        Assert.Equal("Select all", selectAll.GetAttribute("aria-label"));
        Assert.Equal("Select all", selectAll.GetAttribute("data-tooltip"));

        await cut.InvokeAsync(() => selectAll.Click());

        // Like the "All resources" row, it navigates to the resources without a selection. The router then gives the
        // layout the new location.
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        cut.Render();
        cut.WaitForAssertion(() => Assert.Equal("All resources", cut.Find(".resource-header-title").TextContent.Trim()));
        selectAll = cut.Find(".resource-select-all-toggle");
        Assert.Equal("true", selectAll.GetAttribute("aria-pressed"));
        Assert.Equal("All selected", selectAll.GetAttribute("data-tooltip"));

        // Every row looks selected, since everything is. Only an explicit selection marks rows as the current ones.
        Assert.Equal(["api", "cache"], cut.FindAll(".resource-row.selected[data-resource-name]").Select(r => r.GetAttribute("data-resource-name")));
        Assert.Empty(cut.FindAll(".resource-row-link[aria-current]"));
    }

    [Fact]
    public async Task Metrics_ChangingTheSelectionStaysOnMetricsEvenWithoutTelemetry()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.MetricsUrl("api"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // Neither resource sends telemetry, which would otherwise send a selection to the overview.
        await cut.InvokeAsync(() => cut.FindAll(".resource-row-link").Single(link => link.GetAttribute("href") == DashboardUrls.MetricsUrl("cache")).Click());
        Assert.Equal("http://localhost/metrics/resource/cache", navigation.Uri);
        cut.Render();

        await cut.InvokeAsync(() => cut.FindAll(".resource-row-link").Single(link => link.GetAttribute("href") == DashboardUrls.MetricsUrl("api"))
            .Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { CtrlKey = true }));
        Assert.Equal("http://localhost/metrics/resource/cache?resource=cache&resource=api", navigation.Uri);
        cut.Render();

        await cut.InvokeAsync(() => cut.Find(".resource-select-all-toggle").Click());
        Assert.Equal("http://localhost/metrics", navigation.Uri);
    }

    [Theory]
    [InlineData(true, "resources")]
    [InlineData(false, "structuredlogs")]
    public void RootUrl_RedirectsToResourcesWithAllResourcesSelected(bool resourceServiceEnabled, string expectedUrl)
    {
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: resourceServiceEnabled));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/");

        Render<Aspire.Dashboard.Components.Pages.Home>();

        Assert.Equal(expectedUrl, navigationManager.ToBaseRelativePath(navigationManager.Uri));
    }

    [Theory]
    [InlineData("/resources/catalogservice-old", "resources/catalogservice-new")]
    [InlineData("/resources?resource=catalogservice-old&resource=gone-old&resource=cache", "resources?resource=catalogservice-new&resource=cache")]
    // Nothing carries over, so all resources are shown.
    [InlineData("/resources/gone-old", "resources")]
    public void RunSwitch_CarriesSelectionToTheSameResourcesInTheNewRun(string url, string expectedUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("catalogservice-new", displayName: "catalogservice"),
                ModelTestHelpers.CreateResource("cache")
            ], resourceChannelProvider: () => channel));
        // The selection was made in another run, where the replica had a different random suffix.
        var paneState = Services.GetRequiredService<ResourcePaneState>();
        paneState.SelectionRunId = "previous-run";
        paneState.SelectionPersistentKeys = new Dictionary<string, string>
        {
            ["catalogservice-old"] = "catalogservice_0",
            ["gone-old"] = "gone_0",
            ["cache"] = "cache_0"
        };
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(url);

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal(expectedUrl, navigationManager.ToBaseRelativePath(navigationManager.Uri)));
        Assert.Equal(Services.GetRequiredService<DashboardDataSource>().SelectedRun.RunId, paneState.SelectionRunId);
    }

    [Fact]
    public async Task AppHost_NewResourcesAppendAndRunSpecificNamesKeepTheirPosition()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var originalApi = ModelTestHelpers.CreateResource("api-first-run", displayName: "api");
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [originalApi, ModelTestHelpers.CreateResource("cache")], resourceChannelProvider: () => channel));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-drag-handle]").Count));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("cache", originalApi.Name, after: false)));

        await channel.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("aaa"))]);
        cut.WaitForAssertion(() => Assert.Equal(["cache", "api-first-run", "aaa"],
            cut.FindAll("[data-resource-name]").Select(row => row.GetAttribute("data-resource-name"))));
        await channel.Writer.WriteAsync([
            new(ResourceViewModelChangeType.Delete, originalApi),
            new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("api-second-run", displayName: "api"))
        ]);
        cut.WaitForAssertion(() => Assert.Equal(["cache", "api-second-run", "aaa"],
            cut.FindAll("[data-resource-name]").Select(row => row.GetAttribute("data-resource-name"))));
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("types")]
    [InlineData("states")]
    [InlineData("health")]
    [InlineData("Tags")]
    [InlineData("ResourceType")]
    [InlineData("State")]
    public async Task AppHost_ReorderingRequiresNoSearchFiltersOrGrouping(string mode)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Exited)
            ], resourceChannelProvider: () => channel));
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-drag-handle]").Count));
        var positions = Services.GetRequiredService<ResourceOrderStore>().GetPositions();
        Func<Task> reset;
        if (mode == "Search")
        {
            var input = cut.FindComponent<FluentTextInput>();
            await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("a"));
            reset = () => input.Instance.ValueChanged.InvokeAsync(string.Empty);
        }
        else if (mode is "types" or "states" or "health")
        {
            var filterPopover = OpenFilter(cut);
            await SetAllFilterValuesAsync(cut, filterPopover, mode, isVisible: false);
            reset = () => SetAllFilterValuesAsync(cut, filterPopover, mode, isVisible: true);
        }
        else
        {
            cut.Find("#resourcePaneGroupingButton").Click();
            var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.MenuButtonId == "resourcePaneGroupingButton");
            await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == $"resource-grouping-{mode}").OnClick!);
            reset = menu.Instance.Items.Single(item => item.Id == "resource-grouping-None").OnClick!;
        }

        Assert.Empty(cut.FindAll("[data-resource-drag-handle]"));
        Assert.Equal("false", cut.Find(".resource-pane-list").GetAttribute("data-reorder-enabled"));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("api", "cache", after: false)));
        Assert.Equal(positions, Services.GetRequiredService<ResourceOrderStore>().GetPositions());
        await cut.InvokeAsync(reset);
        Assert.Equal(2, cut.FindAll("[data-resource-drag-handle]").Count);
        Assert.Equal("true", cut.Find(".resource-pane-list").GetAttribute("data-reorder-enabled"));
    }

    [Theory]
    [InlineData("Persistent", ResourceListDensity.Comfortable, "Container (persistent)")]
    [InlineData("Persistent", ResourceListDensity.Compact, "Container (persistent)")]
    [InlineData("Persistent", ResourceListDensity.Spacious, "Container (persistent)")]
    [InlineData("Session", ResourceListDensity.Comfortable, "Container")]
    [InlineData("Session", ResourceListDensity.Compact, "Container")]
    [InlineData("Session", ResourceListDensity.Spacious, "Container")]
    [InlineData(null, ResourceListDensity.Comfortable, "Container")]
    [InlineData(null, ResourceListDensity.Compact, "Container")]
    [InlineData(null, ResourceListDensity.Spacious, "Container")]
    public async Task AppHost_ContainerLifetimeQualifiesTypeAndBadgesResourceGlyph(string? lifetime, ResourceListDensity density, string expectedType)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, properties: lifetime is null ? [] : new()
            {
                [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString(lifetime), false, null, 0, null, false)
            })], resourceChannelProvider: () => channel));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(density));

        var type = cut.Find("[data-resource-name='cache'] .resource-row-type");
        Assert.Equal(expectedType, type.TextContent);
        Assert.Equal(expectedType, type.GetAttribute("data-tooltip"));
        Assert.Equal($"Running; {expectedType}", cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("aria-description"));
        var badges = cut.FindAll("[data-resource-name='cache'] .resource-row-icon .persistent-container-badge");
        Assert.Equal(lifetime == "Persistent" ? 1 : 0, badges.Count);
        if (lifetime == "Persistent")
        {
            var badge = Assert.Single(badges);
            Assert.Equal("img", badge.GetAttribute("role"));
            const string description = "This container is persistent and won't be stopped when the app host is shut down.";
            Assert.Equal(description, badge.GetAttribute("aria-label"));
            Assert.Equal(description, badge.GetAttribute("data-tooltip"));
        }
        Assert.Empty(cut.FindAll(".persistent-container-icon"));
    }

    [Theory]
    [InlineData("Tags", new[] { "Backend", "Shared", "Untagged" })]
    [InlineData("ResourceType", new[] { "Container", "Project" })]
    [InlineData("State", new[] { "Exited", "Running", "Unknown" })]
    public async Task AppHost_GroupingSupportsSelectionCollapseSearchAndReset(string grouping, string[] expectedGroups)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("worker", state: KnownResourceState.Exited, resourceType: "Project"),
                ModelTestHelpers.CreateResource("cache", resourceType: "Container")
            ], resourceChannelProvider: () => channel));
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api", "Backend");
        tags.AddTag("worker", "Backend");
        tags.AddTag("api", "Shared");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.Empty(cut.FindAll(".resource-tag-group"));

        cut.Find("#resourcePaneGroupingButton").Click();
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.MenuButtonId == "resourcePaneGroupingButton");
        Assert.Equal(["None", "Tags", "Resource type", "State"], menu.Instance.Items.Where(item => !item.IsHeader).Select(item => item.Text));
        Assert.All(menu.Instance.Items.Where(item => !item.IsHeader), item => Assert.Equal(MenuItemRole.Radio, item.Role));
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == $"resource-grouping-{grouping}").OnClick!);
        Assert.Equal(expectedGroups, cut.FindAll(".resource-tag-group").Select(group => group.GetAttribute("aria-label")));
        Assert.Equal("http://localhost/structuredlogs/resource/api", navigation.Uri);
        Assert.Equal("3 resources", cut.Find(".resource-pane-foot > span").GetAttribute("aria-label"));

        var group = cut.FindAll(".resource-tag-group")[0];
        var groupName = group.GetAttribute("aria-label");
        var resourceNames = group.QuerySelectorAll(".resource-row-name").Select(name => name.TextContent.Trim()).ToList();
        Assert.NotEmpty(resourceNames);
        group.QuerySelector(".resource-row-toggle")!.Click();
        Assert.Empty(cut.FindAll(".resource-tag-group")[0].QuerySelectorAll(".resource-row"));
        cut.FindAll(".resource-tag-group")[0].QuerySelector(".resource-row-toggle")!.Click();
        Assert.Equal(resourceNames, cut.FindAll(".resource-tag-group")[0].QuerySelectorAll(".resource-row-name").Select(name => name.TextContent.Trim()));

        cut.FindAll(".resource-tag-group")[0].QuerySelector(".resource-tag-link")!.Click();
        cut.Render();
        Assert.Equal(resourceNames, cut.Instance.SelectedResourceNames);
        await cut.FindComponent<FluentTextInput>().InvokeAsync(() => cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync(groupName));
        Assert.Equal(groupName, Assert.Single(cut.FindAll(".resource-tag-group")).GetAttribute("aria-label"));
        await cut.FindComponent<FluentTextInput>().InvokeAsync(() => cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync(string.Empty));

        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == "resource-grouping-None").OnClick!);
        Assert.Empty(cut.FindAll(".resource-tag-group"));
        Assert.Equal(["cache", "api", "worker"], cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-name").Select(name => name.TextContent.Trim()));
    }

    [Theory]
    [InlineData("Tags")]
    [InlineData("ResourceType")]
    [InlineData("State")]
    public async Task AppHost_GroupingIsPersistedIndependentlyOfPaneNavigation(string grouping)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, grouping) : (false, null);
        string? storedGrouping = null;
        storage.OnSetUnprotectedAsync = (key, value) =>
        {
            if (key == BrowserStorageKeys.ResourcePaneGrouping)
            {
                storedGrouping = Assert.IsType<string>(value);
            }
        };
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.ResourceListUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-tag-group")));
        navigation.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        cut.Render();
        Assert.Single(cut.FindAll("#resourcePaneGroupingButton"));
        Assert.Single(cut.FindAll(".resource-tag-group"));
        navigation.NavigateTo(DashboardUrls.ResourceListUrl());
        cut.Render();
        Assert.Single(cut.FindAll(".resource-tag-group"));

        cut.Find("#resourcePaneGroupingButton").Click();
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.MenuButtonId == "resourcePaneGroupingButton");
        Assert.Equal($"resource-grouping-{grouping}", Assert.Single(menu.Instance.Items, item => item.Checked).Id);
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == "resource-grouping-None").OnClick!);
        Assert.Equal("None", storedGrouping);
        Assert.Empty(cut.FindAll(".resource-tag-group"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppHost_LegacyTagsViewSelectsAndPersistsTagGrouping(bool fromStorage)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => fromStorage && key == BrowserStorageKeys.ResourcePaneMode ? (true, "Tags") : (false, null);
        string? storedGrouping = null;
        storage.OnSetUnprotectedAsync = (key, value) =>
        {
            if (key == BrowserStorageKeys.ResourcePaneGrouping)
            {
                storedGrouping = Assert.IsType<string>(value);
            }
        };
        Services.GetRequiredService<ResourceTagStore>().AddTag("api", "Backend");
        Services.GetRequiredService<NavigationManager>().NavigateTo(fromStorage ? DashboardUrls.ResourceListUrl() : DashboardUrls.TagsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Backend", Assert.Single(cut.FindAll(".resource-tag-group")).GetAttribute("aria-label"));
            Assert.Equal(ResourcePaneMode.Resources, Services.GetRequiredService<ResourcePaneState>().Mode);
            Assert.Equal("Tags", storedGrouping);
        });
    }

    [Fact]
    public async Task AppHost_StateGroupingCombinesUnknownStatesAndTracksResourceUpdates()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("worker", state: KnownResourceState.Unknown),
                ModelTestHelpers.CreateResource("hidden", state: KnownResourceState.Running, hidden: true),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter)
            ], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "State") : (false, null);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.StructuredLogsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            var group = Assert.Single(cut.FindAll(".resource-tag-group"));
            Assert.Equal("Unknown", group.GetAttribute("aria-label"));
            Assert.Equal(["api", "worker"], group.QuerySelectorAll(".resource-row-name").Select(name => name.TextContent.Trim()));
        });

        await channel.Writer.WriteAsync([new ResourceViewModelChange(ResourceViewModelChangeType.Upsert,
            ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running))]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Unknown", "Running"], cut.FindAll(".resource-tag-group").Select(group => group.GetAttribute("aria-label")));
            Assert.Equal(["worker", "api"], cut.FindAll(".resource-tag-group .resource-row-name").Select(name => name.TextContent.Trim()));
        });
    }

    [Fact]
    public void AppHost_FilterButtonOpensPopoverAndShowsTooltipOnButton()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        var button = cut.Find("#resourcePaneFilterButton");
        Assert.Equal("Filters", button.GetAttribute("data-tooltip"));
        Assert.Equal("Filters", button.GetAttribute("aria-label"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-filter"));
        button.Click();

        Assert.Equal("true", cut.Find("#resourcePaneFilterButton").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".resource-filter"));
        Assert.Equal(["Resource types", "State", "Health state", "Tags"], cut.FindAll(".resource-filter-group-name").Select(name => name.TextContent.Trim()));
    }

    [Theory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces/detail/abc")]
    [InlineData("/metrics")]
    public async Task Standalone_RendersTelemetryResources(string path)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: false, whenConnected: Task.FromCanceled(new CancellationToken(canceled: true)));
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, client);
        await AddTelemetryAsync("api");
        await AddTelemetryAsync("worker");
        Services.GetRequiredService<ResourcePaneState>().SetMode(ResourcePaneMode.Tags);
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsLoaded);
            Assert.Equal(["api", "worker"], cut.Instance.GetAllResourceNames());
            Assert.Equal(3, cut.FindAll(".resource-row").Count);
            Assert.Equal("2 telemetry sources", cut.Find(".resource-pane-foot > span").GetAttribute("aria-label"));
        });
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }

    [Fact]
    public async Task Standalone_NewTelemetryAppearsAndSearchFiltersResources()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.StructuredLogsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        await AddTelemetryAsync("api");
        await AddTelemetryAsync("worker");
        cut.WaitForAssertion(() => Assert.Equal(["api", "worker"], cut.Instance.GetAllResourceNames()));

        await cut.FindComponent<FluentTextInput>().InvokeAsync(() =>
            cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync("WORK"));

        cut.WaitForAssertion(() =>
        {
            var links = cut.FindAll(".resource-row-link");
            Assert.Equal([DashboardUrls.StructuredLogsUrl(), DashboardUrls.StructuredLogsUrl("worker")],
                links.Select(link => link.GetAttribute("href")));
            Assert.Equal("1 telemetry source", cut.Find(".resource-pane-foot > span").GetAttribute("aria-label"));
            Assert.Equal(["api", "worker"], cut.Instance.GetAllResourceNames());
        });
    }

    [Fact]
    public async Task Standalone_SelectingResourcesPreservesSelectionInPageUrls()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        await AddTelemetryAsync("api");
        await AddTelemetryAsync("worker");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.MetricsUrl("api"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.Find(".resource-row-all a").Click();
        Assert.Equal("http://localhost/metrics", navigation.Uri);
        cut.Render();
        cut.FindAll(".resource-row-link").Single(link => link.GetAttribute("href") == DashboardUrls.MetricsUrl("api")).Click();
        cut.Render();
        cut.FindAll(".resource-row-link").Single(link => link.GetAttribute("href") == DashboardUrls.MetricsUrl("worker")).Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { CtrlKey = true });
        cut.Render();

        Assert.Equal(["api", "worker"], cut.Instance.SelectedResourceNames);
        Assert.Equal(2, cut.Instance.GetSelectionTelemetryKeys(cut.Instance.SelectedResourceNames)?.Count);
        Assert.Equal("/traces?resource=api&resource=worker", cut.Instance.GetTabUrl(ResourceTab.Traces));
    }

    [Fact]
    public async Task Standalone_ModifierClickAfterSelectAll_DeselectsOnlyThatResource()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        await AddTelemetryAsync("api");
        await AddTelemetryAsync("frontend");
        await AddTelemetryAsync("worker");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.MetricsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Empty(cut.Instance.SelectedResourceNames));

        cut.FindAll(".resource-row-link").Single(link => link.GetAttribute("href") == DashboardUrls.MetricsUrl("frontend")).Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { MetaKey = true });
        cut.Render();

        Assert.Equal(["api", "worker"], cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public async Task Standalone_ChangingViewportPreservesLayoutAndRoute()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop, new TestDashboardClient(isEnabled: false));
        await AddTelemetryAsync("api");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<CascadingValue<ViewportInformation>>(builder =>
            builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        var layout = cut.FindComponent<ResourcesLayout>().Instance;

        cut.Render(builder => builder.Add(p => p.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Same(layout, cut.FindComponent<ResourcesLayout>().Instance);
        Assert.Equal("http://localhost/structuredlogs/resource/api", navigation.Uri);
        cut.Find(".resource-pane-open-button").Click();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));

        cut.Render(builder => builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Same(layout, cut.FindComponent<ResourcesLayout>().Instance);
        Assert.Equal("http://localhost/structuredlogs/resource/api", navigation.Uri);
    }

    [Fact]
    public async Task AppHost_TelemetryPanePreservesExternalSelectionAndNavigationContext()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, client);
        await AddTelemetryAsync("api");
        await AddTelemetryAsync("browser");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.StructuredLogsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Instance.IsTelemetryPane);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
            Assert.Equal(["api"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
            Assert.Equal([DashboardUrls.StructuredLogsUrl("api")],
                cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("href")));
        });

        navigation.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
            Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
            Assert.Equal(["/structuredlogs/resource/api"],
                cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("href")));
        });
        navigation.NavigateTo(DashboardUrls.AddTelemetryPane(DashboardUrls.TracesUrl("browser")));
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal("/traces/resource/browser", cut.Instance.GetTabUrl(ResourceTab.Traces));
            Assert.Equal("/metrics/resource/browser?duration=15&pane=telemetry",
                cut.Instance.AddSelectionToUrl("/metrics/resource/browser?duration=15"));
            Assert.Null(cut.Instance.GetSelectionTelemetryKeys(null));
        });

        navigation.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        cut.Render();
        await AddTelemetryAsync("worker");
        cut.WaitForAssertion(() => Assert.Equal(["browser", "worker"],
            cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name)));

        navigation.NavigateTo(DashboardUrls.ResourceListUrl());
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Instance.IsTelemetryPane);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
        });
    }

    [Fact]
    public void AppHost_EmptyTelemetryPaneStillShowsAppModelResources()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".resource-pane-message"));
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
            Assert.Empty(cut.Instance.GetSelectionTelemetryKeys(null)!);
            Assert.Equal(3, cut.FindAll(".resource-kind-toggle").Count);
        });
    }

    [Fact]
    public async Task AppHost_DirectTelemetrySourceLinkSelectsTelemetryPane()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        await AddTelemetryAsync("browser");
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.TracesUrl("browser"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
            Assert.Equal("/traces/detail/abc?pane=telemetry", cut.Instance.AddPaneToUrl("/traces/detail/abc"));
        });
    }

    [Fact]
    public void AppHost_StillSubscribesToAppModelResources()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsLoaded);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
        });
        Assert.Equal(1, client.ResourceSubscriptionCount);
        cut.Find(".resource-pane-open-button").Click();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
    }

    private Task AddTelemetryAsync(string name)
    {
        var resource = CreateResource(name);
        resource.Attributes.Remove(resource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
        return Services.GetRequiredService<SqliteTelemetryRepository>().AddLogsAsync(new AddContext(),
        [
            new ResourceLogs
            {
                Resource = resource,
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord() } } }
            }
        ]);
    }
}
