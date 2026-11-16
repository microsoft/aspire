// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutHomeTests : DashboardTestContext
{
    // A tab's content is a page scoped by the resource list beside it, not a pane over the table, so no page has a
    // close button. The table is the home page, reached from the application name or by expanding the list. Escape,
    // which used to press the close button, still leads there from every page (see ResourcesLayout.razor.js).
    [Theory]
    [InlineData("/resources")]
    [InlineData("/resources/api")]
    [InlineData("/resources/api?terminal=true")]
    [InlineData("/resources?resource=api&resource=cache")]
    [InlineData("/terminals/resource/api")]
    [InlineData("/consolelogs")]
    [InlineData("/consolelogs/resource/api")]
    [InlineData("/structuredlogs/resource/api?logLevel=Error")]
    [InlineData("/traces?type=server")]
    [InlineData("/traces/detail/missing?resource=api&type=server&filters=example&spanId=span")]
    [InlineData("/metrics/resource/api/meter/Test/instrument/Requests?duration=30")]
    public void Pages_HaveNoCloseButtonAndEscapeGoesHome(string url)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Empty(cut.FindAll(".resource-header-controls .detail-page-close"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal("true", cut.Find(".resources-layout").GetAttribute("data-escape-home"));
    }

    // Escape goes home only where there's a table to go to and it isn't already showing or loading. On a narrow layout
    // "/" redirects to the overview, and without the resource service it redirects to the structured logs (see Home).
    [Theory]
    [InlineData("home")]
    [InlineData("narrow")]
    [InlineData("no-resource-service")]
    [InlineData("home-loading")]
    public async Task EscapeHome_IsOffWhereItWouldNotReachTheTable(string scenario)
    {
        var viewport = new ViewportInformation(IsDesktop: scenario != "narrow", IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, scenario == "no-resource-service"
            ? new TestDashboardClient(isEnabled: false)
            : new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource("api")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(scenario switch
        {
            "home" => "/",
            "no-resource-service" => "/structuredlogs",
            _ => "/resources/api"
        });
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        if (scenario == "home-loading")
        {
            Assert.Equal("true", cut.Find(".resources-layout").GetAttribute("data-escape-home"));
            // What the brand does before it navigates home (see MainLayout.GoToAllResourcesAsync).
            await cut.InvokeAsync(cut.Instance.OnHomeNavigationStarting);
        }

        Assert.Null(cut.Find(".resources-layout").GetAttribute("data-escape-home"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?unrelated=value")]
    public void Home_ShowsTheResourceListAsTheFullPageTable(string url)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var header = Render<SectionOutlet>(p => p.Add(o => o.SectionName, MainLayout.HeaderContextSectionName));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal(["api", "cache"], cut.FindAll("[data-resource-name]").Select(row => row.GetAttribute("data-resource-name")));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        // The table isn't one of the tabs, so none is shown selected.
        Assert.Equal("resource-tab-none", header.FindComponent<FluentTabs>().Instance.ActiveTabId);
        // Home has its own URL. It doesn't redirect to the overview.
        Assert.Equal(url.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
    }

    [Theory]
    [InlineData(true)]
    // A narrow layout has no full-page table, and "/" gives way to the overview of all resources (see Home).
    [InlineData(false)]
    public void Home_DoesNotRestoreTheRememberedSelection(bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.LastSelectedResources ? (true, new List<string> { "api" }) : (false, null)
        };
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ISessionStorage>(sessionStorage));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // The overview restores the last selection when it's entered without one. Home lists every resource and has
        // no selection of its own, so it stays where it is.
        Assert.Equal(string.Empty, navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(isDesktop, cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Empty(cut.Instance.SelectedResourceNames);

        // On a narrow layout, home's redirect then opens the overview of all resources, still without the selection.
        navigation.NavigateTo(DashboardUrls.ResourceListUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
        cut.Render();
        Assert.Equal("resources", navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public void Home_HasNoListControls()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // The table isn't a list beside a page, so there's nothing to hide or dock. It's left by choosing a row,
        // "All resources" or a tab, and reached from the brand in the header.
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Empty(cut.FindAll(".resource-pane-toggle-button"));
        Assert.Equal(["resourcePaneFilterButton", "resourcePaneGroupingButton"],
            cut.FindAll(".resource-pane-head fluent-button").Select(button => button.Id));
    }

    [Theory]
    [InlineData("/resources")]
    [InlineData("/resources/api")]
    [InlineData("/traces/resource/api?type=server")]
    public void DockedList_IsHiddenFromThePageTitleRow(string url)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // The one control for the docked list's width is the toggle beside the page title. The list's head only
        // filters and groups.
        Assert.NotNull(Assert.Single(cut.FindAll(".resource-pane-toggle-button")).Closest(".resource-header-row"));
        Assert.Equal(["resourcePaneFilterButton", "resourcePaneGroupingButton"],
            cut.FindAll(".resource-pane-head fluent-button").Select(button => button.Id));
    }

    [Fact]
    public async Task Home_SelectingARowOpensItsOverviewBesideTheDockedList()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        cut.Find(".resource-row[data-resource-name='cache'] .resource-row-link").Click();
        cut.Render();
        await cut.InvokeAsync(() => cut.Instance.CompletePaneSelectionReveal(navigation.Uri));

        Assert.Equal("resources/cache", navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Fact]
    public async Task Home_AllResourcesRowOpensTheOverviewOfAllResources()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));

        await cut.Find(".resource-row-all .resource-row-link").ClickAsync();
        cut.Render();

        Assert.Equal("resources", navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
    }

    [Fact]
    public async Task CurrentTab_ChosenWhileHomeIsLoadingOpensForAllResources()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // What the brand does before it navigates home (see MainLayout.GoToAllResourcesAsync). The layout marks itself,
        // so its script reports a click on the current tab, which FluentTabs doesn't.
        await cut.InvokeAsync(cut.Instance.OnHomeNavigationStarting);
        Assert.Equal("true", cut.Find(".resources-layout").GetAttribute("data-home-pending"));
        await cut.InvokeAsync(() => cut.Instance.OpenTabFromFullCanvasAsync("tab-Overview"));
        Assert.Equal("resources/api", navigation.ToBaseRelativePath(navigation.Uri));

        navigation.NavigateTo("/");
        cut.Render();

        // The overview opens for home's selection, every resource, rather than the table staying open.
        cut.WaitForAssertion(() => Assert.Equal("resources", navigation.ToBaseRelativePath(navigation.Uri)));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Null(cut.Find(".resources-layout").GetAttribute("data-home-pending"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabMenu_ChoiceWhileHomeIsLoadingOpensForAllResources(bool homeLandsFirst)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // The overflow and compact menus are built for the selection they're opened with, here "api".
        var header = Render<SectionOutlet>(p => p.Add(o => o.SectionName, MainLayout.HeaderContextSectionName));
        var menu = header.FindComponents<AspireMenuButton>().First(button => button.Instance.ButtonClass == "resource-tabs-compact-button");
        var console = menu.Instance.ItemsProvider().Single(item => item.Id == "tab-Console-menu-item");

        // What the brand does before it navigates home (see MainLayout.GoToAllResourcesAsync).
        await cut.InvokeAsync(cut.Instance.OnHomeNavigationStarting);
        if (homeLandsFirst)
        {
            navigation.NavigateTo("/");
            cut.Render();
            await cut.InvokeAsync(console.OnClick!);
        }
        else
        {
            // Home is still loading: the choice waits for it, then opens for home's selection.
            await cut.InvokeAsync(console.OnClick!);
            Assert.Equal("resources/api", navigation.ToBaseRelativePath(navigation.Uri));
            navigation.NavigateTo("/");
            cut.Render();
        }

        cut.WaitForAssertion(() => Assert.Equal("consolelogs", navigation.ToBaseRelativePath(navigation.Uri)));
        Assert.Empty(cut.Instance.SelectedResourceNames);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("all")]
    [InlineData("navigation")]
    public async Task Home_LeavingDocksAListThatWasHidden(string exit)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        var stored = new Dictionary<string, object?> { [BrowserStorageKeys.ResourcePaneCollapsed] = true };
        storage.OnGetUnprotectedAsync = key => stored.TryGetValue(key, out var value) ? (true, value) : (false, null);
        storage.OnSetUnprotectedAsync = (key, value) => stored[key] = value;
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        // The list was hidden beside other pages, but home is the list itself, across the page.
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        switch (exit)
        {
            case "row":
                cut.Find(".resource-row[data-resource-name='cache'] .resource-row-link").Click();
                break;
            case "all":
                await cut.Find(".resource-row-all .resource-row-link").ClickAsync();
                break;
            default:
                navigation.NavigateTo("/structuredlogs");
                break;
        }
        cut.Render();

        // Leaving home docks the list it showed, and remembers that, rather than hiding it again.
        cut.WaitForAssertion(() => Assert.Equal(false, stored[BrowserStorageKeys.ResourcePaneCollapsed]));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-collapsed"));
        Assert.Single(cut.FindAll(".resources-layout > .resource-pane"));
    }

    [Theory]
    [InlineData("/resources")]
    [InlineData("/structuredlogs")]
    [InlineData("/traces?resource=api")]
    [InlineData("/traces/detail/missing")]
    public void Home_NavigatingToAPageOpensItsContent(string nextUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        navigation.NavigateTo(nextUrl);
        cut.Render();

        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        // Going back to home, for example with the browser's back button, shows the table again.
        navigation.NavigateTo("/");
        cut.Render();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
    }

    [Theory]
    [InlineData("/resources", "/resources")]
    [InlineData("/resources/api", "/resources")]
    [InlineData("/consolelogs", "/consolelogs")]
    [InlineData("/structuredlogs", "/structuredlogs")]
    [InlineData("/traces", "/traces")]
    [InlineData("/traces/detail/missing", "/traces")]
    [InlineData("/Traces/Detail/missing", "/traces")]
    [InlineData("/metrics", "/metrics")]
    [InlineData("/metrics?meter=Test&duration=30", "/metrics")]
    [InlineData("/metrics?meter=Test&instrument=Requests&duration=30", "/metrics")]
    [InlineData("/metrics/resource/api/meter/Test", "/metrics")]
    [InlineData("/metrics/resource/api/meter/Test/instrument/Requests", "/metrics")]
    [InlineData("/traces?type=server", "/traces?type=server")]
    [InlineData("/structuredlogs?logLevel=Error", "/structuredlogs?logLevel=Error")]
    [InlineData("/metrics?duration=30", "/metrics?duration=30")]
    public async Task ResourceDropdown_OpensAndClosesDrawer(string url, string expectedDestination)
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // On a narrow layout the list is a drawer opened from the page title, so it has no docking toggle.
        Assert.Empty(cut.FindAll(".resource-pane-toggle-button"));
        Assert.True(cut.Find(".resource-pane").HasAttribute("hidden"));
        await cut.Find(".resource-picker").ClickAsync();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resource-pane").HasAttribute("hidden"));

        await cut.Find($"[aria-label='{Resources.Layout.ResourcePaneClose}']").ClickAsync();
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal(url.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));

        await cut.Find(".resource-picker").ClickAsync();
        await cut.Find(".resource-row-all .resource-row-link").ClickAsync();
        cut.Render();
        Assert.Equal(expectedDestination.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Fact]
    public async Task ViewportChange_SwitchesBetweenListToggleAndResourceDropdown()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = desktop with { IsDesktop = false };
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");
        var cut = Render<CascadingValue<ViewportInformation>>(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ResourcesLayout>().Instance.IsLoaded));
        Assert.Single(cut.FindAll(".resource-pane-toggle-button"));

        cut.Render(p => p.Add(v => v.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Empty(cut.FindAll(".resource-pane-toggle-button"));
        await cut.Find(".resource-picker").ClickAsync();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        cut.Render(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Single(cut.FindAll(".resource-pane-toggle-button"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Fact]
    public void TraceLinks_CarrySelectionAndListFiltersToNewTabs()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/traces?resource=api&resource=cache&type=server&filters=example");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        navigation.NavigateTo(cut.Instance.GetTraceDetailUrl("trace", "span"));
        cut.Render();

        Assert.Equal(["api", "cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal("/traces?resource=api&resource=cache&type=server&filters=example", cut.Instance.DetailParentUrl);
    }

    [Fact]
    public async Task TraceActionsViewDetails_CarriesSelectionAndListFilters()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/traces?resource=api&resource=cache&type=server&filters=example");
        var layout = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        layout.WaitForAssertion(() => Assert.True(layout.Instance.IsLoaded));

        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var summary = new TraceSummary
        {
            TraceId = "trace",
            LastUpdatedTimestampTicks = 0,
            FullName = "api: GET /",
            StartTime = DateTime.UnixEpoch,
            Duration = TimeSpan.Zero,
            RootResource = new OtlpResource("api", "instance", uninstrumentedPeer: false, context),
            Resources = [],
            HasError = false,
            HasGenAI = false
        };
        var actions = Render<TraceActions>(p => p
            .Add(a => a.Summary, summary)
            .AddCascadingValue(layout.Instance));

        var viewDetails = Assert.Single(actions.FindComponent<AspireMenuButton>().Instance.ItemsProvider(),
            item => item.Text == Resources.ControlsStrings.ActionViewDetailsText);
        await actions.InvokeAsync(viewDetails.OnClick!);

        Assert.Equal(navigation.ToAbsoluteUri(layout.Instance.GetTraceDetailUrl("trace")).AbsoluteUri, navigation.Uri);
        Assert.Equal("traces/detail/trace?resource=api&resource=cache&type=server&filters=example", navigation.ToBaseRelativePath(navigation.Uri));
    }
}
