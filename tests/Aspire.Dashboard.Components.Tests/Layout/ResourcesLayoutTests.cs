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
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutTests : DashboardTestContext
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CollapsedPane_PickerSelectsResourceAndCloses(bool appHostEnabled, bool selected)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: appHostEnabled,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, true) : (false, null);
        if (!appHostEnabled)
        {
            await AddTelemetryAsync("api");
            await AddTelemetryAsync("cache");
        }
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(appHostEnabled
            ? selected ? DashboardUrls.ResourceOverviewUrl("api") : DashboardUrls.ResourceListUrl()
            : DashboardUrls.StructuredLogsUrl(selected ? "api" : null));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        if (appHostEnabled && !selected)
        {
            // The dashboard opens on the full-page list. "All resources" opens their overview next to the collapsed pane.
            Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
            await cut.InvokeAsync(() => cut.Find(".resource-row-all .resource-row-link").Click());
        }

        var picker = cut.Find("#resourcePanePickerButton");
        Assert.Equal(selected ? "api" : appHostEnabled ? "All resources" : "All sources", picker.TextContent.Trim());
        Assert.Equal("dialog", picker.GetAttribute("aria-haspopup"));
        Assert.Equal("false", picker.GetAttribute("aria-expanded"));
        var icon = Assert.Single(picker.QuerySelectorAll(".resource-header-icon"));
        Assert.NotNull(icon.ParentElement);
        Assert.Equal("start", icon.ParentElement.GetAttribute("slot"));
        Assert.Empty(cut.FindAll(".resource-header-row > .resource-header-icon"));
        Assert.Empty(cut.FindAll(".resource-pane"));

        await picker.ClickAsync(new MouseEventArgs());

        Assert.Equal("true", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".resource-picker-popover .resource-pane"));
        Assert.Equal("auto", cut.Find(".resource-picker-popover").GetAttribute("popover"));
        Assert.Empty(cut.FindAll(".resource-pane-resizer"));
        Assert.Single(cut.FindAll(".resource-pane-popup .resource-pane-search"));
        Assert.Empty(cut.FindAll("#resourcePaneFilterButton"));
        Assert.Empty(cut.FindAll("#resourcePaneGroupingButton"));
        Assert.False(cut.Find(".resource-pane-popup").HasAttribute("inert"));
        await cut.Find(".resource-row-link[aria-label='cache']").ClickAsync(new MouseEventArgs());
        Assert.Equal(appHostEnabled ? DashboardUrls.ResourceOverviewUrl("cache") : DashboardUrls.StructuredLogsUrl("cache"),
            new Uri(navigation.Uri).PathAndQuery);
        Assert.Equal("false", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-pane"));

        cut.Render();
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal("cache", cut.Find("#resourcePanePickerButton").TextContent.Trim());
        Assert.Contains("pane-collapsed", cut.Find(".resources-layout").ClassList);
    }

    [Theory]
    [InlineData(true, "None")]
    [InlineData(true, "Control")]
    [InlineData(true, "Shift")]
    [InlineData(true, "Meta")]
    [InlineData(false, "None")]
    [InlineData(false, "Control")]
    [InlineData(false, "Shift")]
    [InlineData(false, "Meta")]
    public async Task CollapsedPane_SelectingAllClosesPicker(bool appHostEnabled, string modifier)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: appHostEnabled,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, true) : (false, null);
        if (!appHostEnabled)
        {
            await AddTelemetryAsync("api");
        }
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(appHostEnabled ? DashboardUrls.ResourceOverviewUrl("api") : DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());

        await cut.Find(".resource-row-all .resource-row-link").ClickAsync(new MouseEventArgs
        {
            CtrlKey = modifier == "Control",
            ShiftKey = modifier == "Shift",
            MetaKey = modifier == "Meta"
        });
        cut.Render();

        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.Equal(appHostEnabled ? DashboardUrls.ResourceListUrl() : DashboardUrls.StructuredLogsUrl(), new Uri(navigation.Uri).PathAndQuery);
        Assert.Equal("false", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-pane"));
    }

    [Fact]
    public async Task CollapsedPane_PickerDismissesAndExpandingRestoresSidebar()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, true) : (false, null);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceOverviewUrl("api"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        await cut.InvokeAsync(cut.Instance.OnResourcePickerClosedAsync);

        Assert.Equal("false", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-pane"));
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        await cut.Find(".resource-pane-toggle-button").ClickAsync(new MouseEventArgs());

        Assert.Single(cut.FindAll(".resources-layout > .resource-pane"));
        Assert.Empty(cut.FindAll("#resourcePanePickerButton"));
        Assert.Single(cut.FindAll(".resource-pane-resizer"));
        Assert.Single(cut.FindAll(".resource-header-row > .resource-header-icon"));
        Assert.Single(cut.FindAll("#resourcePaneFilterButton"));
        Assert.Single(cut.FindAll("#resourcePaneGroupingButton"));
        Assert.Equal("api", cut.Find(".resource-title").TextContent.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MobilePicker_UsesDrawerRegardlessOfDesktopCollapsePreference(bool collapsed)
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, collapsed) : (false, null);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        await cut.Find(".resource-pane-open-button").ClickAsync(new MouseEventArgs());

        Assert.Contains("drawer-open", cut.Find(".resources-layout").ClassList);
        Assert.False(cut.Find(".resource-pane").HasAttribute("hidden"));
        Assert.Single(cut.FindAll("#resourcePaneFilterButton"));
        Assert.Single(cut.FindAll("#resourcePaneGroupingButton"));
        Assert.Empty(cut.FindAll("#resourcePanePickerButton"));
        Assert.Empty(cut.FindAll(".resource-picker-popover"));
    }

    [Theory]
    [InlineData(true, "Control")]
    [InlineData(true, "Shift")]
    [InlineData(true, "Meta")]
    [InlineData(false, "Control")]
    [InlineData(false, "Shift")]
    [InlineData(false, "Meta")]
    public async Task CollapsedPane_ModifierSelectionKeepsPickerOpen(bool appHostEnabled, string modifier)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: appHostEnabled,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache"), ModelTestHelpers.CreateResource("worker")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, true) : (false, null);
        if (!appHostEnabled)
        {
            await AddTelemetryAsync("api");
            await AddTelemetryAsync("cache");
            await AddTelemetryAsync("worker");
        }
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(appHostEnabled ? DashboardUrls.ResourceOverviewUrl("api") : DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());

        await cut.Find(".resource-row-link[aria-label='worker']").ClickAsync(new MouseEventArgs
        {
            CtrlKey = modifier == "Control",
            ShiftKey = modifier == "Shift",
            MetaKey = modifier == "Meta"
        });
        cut.Render();
        Assert.Equal(modifier == "Shift" ? ["api", "cache", "worker"] : ["api", "worker"], cut.Instance.SelectedResourceNames);
        Assert.Equal("true", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".resource-picker-popover .resource-pane"));

        await cut.Find(".resource-row-link[aria-label='cache']").ClickAsync(new MouseEventArgs());
        cut.Render();
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal("false", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-pane"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollapsedPane_ModifierDeselectingLastResourceKeepsPickerOpen(bool appHostEnabled)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: appHostEnabled,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, true) : (false, null);
        if (!appHostEnabled)
        {
            await AddTelemetryAsync("api");
            await AddTelemetryAsync("cache");
        }
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(appHostEnabled ? DashboardUrls.ResourceOverviewUrl("api") : DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        await cut.Find(".resource-row-link[aria-label='api']").ClickAsync(new MouseEventArgs { CtrlKey = true });
        cut.Render();

        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.Equal("true", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".resource-picker-popover .resource-pane"));

        navigation.NavigateTo(appHostEnabled ? DashboardUrls.ResourceOverviewUrl("cache") : DashboardUrls.StructuredLogsUrl("cache"));
        cut.Render();
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal("false", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll(".resource-pane"));
    }

    [Theory]
    [InlineData("Control")]
    [InlineData("Shift")]
    [InlineData("Meta")]
    public async Task CollapsedPane_ModifierGroupSelectionKeepsPickerOpen(string modifier)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", resourceType: KnownResourceTypes.Project),
                ModelTestHelpers.CreateResource("worker", resourceType: KnownResourceTypes.Project),
                ModelTestHelpers.CreateResource("cache")
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key switch
        {
            BrowserStorageKeys.ResourcePaneCollapsed => (true, true),
            BrowserStorageKeys.ResourcePaneGrouping => (true, nameof(ResourcePaneGrouping.ResourceType)),
            _ => (false, null)
        };
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.ResourceOverviewUrl("api"));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        var click = new MouseEventArgs
        {
            CtrlKey = modifier == "Control",
            ShiftKey = modifier == "Shift",
            MetaKey = modifier == "Meta"
        };
        var groupUrl = DashboardUrls.AddResourceSelection(DashboardUrls.ResourceListUrl(), ["api", "worker"]);
        await cut.FindAll(".resource-tag-link").Single(link => link.GetAttribute("href") == groupUrl).ClickAsync(click);
        cut.Render();

        Assert.Equal(["api", "worker"], cut.Instance.SelectedResourceNames);
        Assert.Equal("true", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        await cut.FindAll(".resource-tag-link").Single(link => link.GetAttribute("href") == groupUrl).ClickAsync(click);
        cut.Render();
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.Equal("true", cut.Find("#resourcePanePickerButton").GetAttribute("aria-expanded"));
        Assert.Single(cut.FindAll(".resource-picker-popover .resource-pane"));
    }

    [Fact]
    public async Task TabOverflowRegistration_UsesStableLayoutIdForCleanup()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["registerTabOverflow"]));

        var registration = Assert.Single(JSInterop.Invocations["registerTabOverflow"]);
        var registrationId = Assert.IsType<string>(registration.Arguments[0]);
        Assert.NotEmpty(registrationId);

        await DisposeComponentsAsync();

        var cleanup = Assert.Single(JSInterop.Invocations["unregisterTabOverflow"]);
        Assert.Equal(registrationId, Assert.IsType<string>(Assert.Single(cleanup.Arguments)));
    }

    [Theory]
    [InlineData(KnownResourceTypes.Project, KnownProperties.Project.Path, "MyFrontend.csproj")]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Image, "postgres:17")]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.Path, "worker.exe")]
    [InlineData("PostgresDatabase", KnownProperties.Resource.Source, "catalogdb")]
    public void FullCanvas_SourceUsesProductValuesAndPreservesEmptyTrack(string type, string property, string value)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var original = type is KnownResourceTypes.Project or KnownResourceTypes.Executable ? Path.Combine("workspace", value) : value;
        var resource = ModelTestHelpers.CreateResource("source", resourceType: type, properties: new()
        {
            [property] = new(property, Value.ForString(original), false, null, 0, null, false)
        });
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [resource, ModelTestHelpers.CreateResource("empty")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.Empty(cut.FindAll(".resource-row-source"));
        ResourceSetupHelpers.ToggleResourceList(cut);
        var source = cut.Find("[data-resource-name='source'] .resource-row-source-value");
        Assert.Equal($"{type} {value}", source.TextContent);
        Assert.Equal(type, source.QuerySelector(".resource-row-source-type")!.TextContent);
        Assert.Equal($"{type}: {original}", source.GetAttribute("data-tooltip"));
        Assert.Equal($"Source: {type} {original}", source.GetAttribute("aria-label"));
        Assert.Equal("0", source.GetAttribute("tabindex"));
        var emptySource = cut.Find("[data-resource-name='empty'] .resource-row-source-value");
        Assert.Equal("Container", emptySource.TextContent);
        Assert.Null(emptySource.GetAttribute("tabindex"));
        Assert.Equal(2, cut.FindAll(".resource-row-source").Count);
        Assert.Empty(cut.FindAll(".resource-relationship-summary, .resource-row-dependencies, .resource-row-references"));
    }

    [Fact]
    public async Task RowTime_ShowsWhenAStoppedResourceStopped()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var started = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        var stopped = started.AddHours(3);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, startTimeStamp: started),
                ModelTestHelpers.CreateResource("worker", state: KnownResourceState.Exited, startTimeStamp: started, stopTimeStamp: stopped)
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var clock = Assert.IsType<TestTimeProvider>(Services.GetRequiredService<BrowserTimeProvider>());
        clock.UtcNow = new DateTimeOffset(stopped).AddMinutes(1);
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Comfortable));

        // A two-line row shows the age after the state. A stopped resource's is how long ago it stopped, which says
        // more than how long ago it started; the tooltip says which.
        var workerTime = cut.Find("[data-resource-name='worker'] .resource-row-sidebar-start time");
        Assert.Equal("1m ago", workerTime.TextContent);
        Assert.Equal(stopped.ToString("O", System.Globalization.CultureInfo.InvariantCulture), workerTime.GetAttribute("datetime"));
        Assert.Equal($"Stopped 1m ago ({FormatHelpers.FormatDateTime(clock, stopped)})", workerTime.GetAttribute("data-tooltip"));
        Assert.Equal(workerTime.GetAttribute("data-tooltip"), workerTime.GetAttribute("aria-label"));
        var apiTime = cut.Find("[data-resource-name='api'] .resource-row-sidebar-start time");
        Assert.Equal($"Started 3h ago ({FormatHelpers.FormatDateTime(clock, started)})", apiTime.GetAttribute("data-tooltip"));

        // The expanded list shows the same short age; the state beside it and the tooltip say which it is.
        ResourceSetupHelpers.ToggleResourceList(cut);
        var wideWorkerTime = cut.Find("[data-resource-name='worker'] .resource-row-wide-metadata time");
        Assert.Equal("1m ago", wideWorkerTime.TextContent.Trim());
        Assert.Equal($"Stopped 1m ago ({FormatHelpers.FormatDateTime(clock, stopped)})", wideWorkerTime.GetAttribute("data-tooltip"));
        Assert.Equal("3h ago", cut.Find("[data-resource-name='api'] .resource-row-wide-metadata time").TextContent.Trim());
    }

    [Fact]
    public async Task RowAges_ShowWhenEveryResourceShowsTheSameAge()
    {
        // Right after the app starts every row says the same age. They still show: hiding them until they differed
        // left the column blank for minutes, which read as the ages not loading.
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var started = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, startTimeStamp: started),
                ModelTestHelpers.CreateResource("cache", state: KnownResourceState.Running, startTimeStamp: started)
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var clock = Assert.IsType<TestTimeProvider>(Services.GetRequiredService<BrowserTimeProvider>());
        clock.UtcNow = new DateTimeOffset(started).AddMinutes(10);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        // Two-line rows show the age after the state; a compact row has no room for it.
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Comfortable));

        Assert.Equal(["10m ago", "10m ago"], cut.FindAll(".resource-row-sidebar-start time").Select(time => time.TextContent));
    }

    [Theory]
    [InlineData(ResourceListDensity.Compact)]
    [InlineData(ResourceListDensity.Comfortable)]
    public async Task WideRowMetadata_PreservesRuntimeStateAndAccessibleHealthWithRelativeStart(ResourceListDensity density)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        const string address = "https://example.test/api?token=synthetic-secret&mode=preview";
        const string tcpAddress = "tcp://example.test:5432?password=synthetic-secret";
        var api = ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, urls:
        [
            new("https", new Uri(address), false, false, new("Friendly API", 0)),
            new("tcp", new Uri(tcpAddress), false, false, UrlDisplayPropertiesViewModel.Empty),
            new("internal", new Uri("https://internal.test"), true, false, UrlDisplayPropertiesViewModel.Empty),
            new("inactive", new Uri("https://inactive.test"), false, true, UrlDisplayPropertiesViewModel.Empty)
        ]);
        var missing = new ResourceViewModel
        {
            Name = "missing",
            DisplayName = "missing",
            ResourceType = KnownResourceTypes.Executable,
            Uid = "missing",
            ReplicaIndex = 0,
            State = null,
            StateStyle = null,
            KnownState = null,
            CreationTimeStamp = null,
            StartTimeStamp = null,
            StopTimeStamp = null,
            Environment = [],
            Urls = [],
            Volumes = [],
            Relationships = [],
            Properties = System.Collections.Immutable.ImmutableDictionary<string, ResourcePropertyViewModel>.Empty,
            Commands = [],
            HealthReports = []
        };
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [api, missing, ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter),
                ModelTestHelpers.CreateResource("finished", state: KnownResourceState.Finished)],
            resourceChannelProvider: () => channel));
        var clock = Assert.IsType<TestTimeProvider>(Services.GetRequiredService<BrowserTimeProvider>());
        clock.UtcNow = new DateTimeOffset(api.StartTimeStamp!.Value).AddMinutes(10);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(density));
        var compact = density is ResourceListDensity.Compact;
        Assert.Empty(cut.FindAll(".resource-row-wide-metadata"));
        Assert.Equal("Running", GetStateText(cut.Find("[data-resource-name='api']")));
        // A compact sidebar row is a single line without the start time. The expanded list below has a column for it.
        Assert.Equal(compact ? [] : ["10m ago"], cut.FindAll("[data-resource-name='api'] time").Select(time => time.TextContent));
        Assert.Equal("Finished", GetStateText(cut.Find("[data-resource-name='finished']")));
        Assert.Empty(cut.FindAll(".resource-pane-canvas-toggle"));
        ResourceSetupHelpers.ToggleResourceList(cut);

        var row = cut.Find("[data-resource-name='api']");
        // The URLs column is the resources grid's: the URL opens in a new tab, and the tcp address isn't a link but takes
        // focus for its tooltip. Addresses are masked. The internal and inactive URLs aren't listed.
        var urls = row.QuerySelector(".resource-row-endpoints .url-container")!;
        var link = Assert.Single(urls.QuerySelectorAll("a"));
        Assert.Equal(address, link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("Friendly API", link.TextContent);
        Assert.Equal(DashboardUIHelpers.MaskUrlForDisplay(address), link.GetAttribute("data-tooltip"));
        var tcp = Assert.Single(urls.QuerySelectorAll("span[tabindex='0']"));
        Assert.Equal(DashboardUIHelpers.MaskUrlForDisplay(tcpAddress), tcp.TextContent);
        // The URLs column has the links, so the row has no open-URL button that would repeat the first one.
        Assert.Empty(row.QuerySelectorAll(".resource-row-primary-endpoint"));
        var health = row.QuerySelector(".resource-row-health")!;
        Assert.Empty(row.QuerySelectorAll(".resource-row-health-status"));
        Assert.Equal("Running", GetStateText(row));
        Assert.Contains("Healthy", row.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
        Assert.Empty(cut.FindAll(".resource-pane-canvas-toggle"));
        var time = health.QuerySelector(".resource-row-start-time time")!;
        Assert.Equal(api.StartTimeStamp!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture), time.GetAttribute("datetime"));
        // Every row repeats its age, so the row shows the short form; the tooltip keeps the full wording.
        Assert.Equal("10m ago", time.TextContent.Trim());
        Assert.Equal($"Started 10m ago ({FormatHelpers.FormatDateTime(clock, api.StartTimeStamp.Value)})", time.GetAttribute("data-tooltip"));
        Assert.Equal("above", time.GetAttribute("data-tooltip-positioning"));
        Assert.Equal("0", time.GetAttribute("tabindex"));
        var absent = cut.Find("[data-resource-name='missing'] .resource-row-wide-metadata");
        // A resource with no source still shows its type in the source column.
        Assert.Equal("Executable", absent.TextContent.Trim());
        Assert.Empty(absent.QuerySelectorAll("time, .url-container"));
        var parameter = cut.Find("[data-resource-name='key'] .resource-row-wide-metadata");
        Assert.Equal(string.Empty, parameter.TextContent.Trim());
        Assert.Empty(parameter.QuerySelectorAll("time, .resource-row-health-status, .url-container"));

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        ResourceSetupHelpers.ToggleResourceList(cut);
        ResourceSetupHelpers.ToggleResourceList(cut);
        cut.WaitForAssertion(() => Assert.Equal("11m ago",
            cut.Find("[data-resource-name='api'] .resource-row-health time").TextContent.Trim()));

        // In the full-page list, a compact row's state dot follows its name rather than ending the wide name column.
        // There is one dot per row.
        if (compact)
        {
            var dot = Assert.Single(cut.FindAll("[data-resource-name='api'] .resource-row-meta-state"));
            Assert.Equal("resource-row-text", dot.ParentElement!.ClassName);
        }

        // Compact rows show the state as a dot that names it in its tooltip. The other densities spell it out.
        string? GetStateText(AngleSharp.Dom.IElement resourceRow) => compact
            ? resourceRow.QuerySelector(".resource-row-meta-state")!.GetAttribute("data-tooltip")
            : resourceRow.QuerySelector(".state-dot + span")!.TextContent;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextMenu_UsesTargetRowActionsWithoutChangingSelection(bool readOnly)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var executed = new System.Collections.Concurrent.ConcurrentQueue<(string Resource, string Command)>();
        static CommandViewModel Command(string name, CommandViewModelState state) => new(name, state, name, "",
            confirmationMessage: "", argumentInputs: [], isHighlighted: false, iconName: "", iconVariant: IconVariant.Regular);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true, isReadOnly: readOnly,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("cache", commands:
                [
                    Command("enabled", CommandViewModelState.Enabled),
                    Command("disabled", CommandViewModelState.Disabled),
                    Command("hidden", CommandViewModelState.Hidden)
                ])
            ], resourceChannelProvider: () => channel, executeResourceCommand: (resource, _, command, _, _) =>
            {
                executed.Enqueue((resource, command.Name));
                return Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Succeeded });
            }));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources?resource=api&resource=cache");
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var originalUrl = navigation.Uri;
        var fallback = cut.FindComponents<AspireMenuButton>().Single(b => b.Instance.Title == "Actions for cache");
        var expected = fallback.Instance.ItemsProvider().Select(item => (item.Text, item.IsDivider, item.IsDisabled)).ToArray();

        await cut.InvokeAsync(() => cut.Instance.OpenResourceContextMenuAsync("cache", 20, 40));

        var menu = cut.FindComponents<AspireMenu>().Single(m => m.Instance.Anchor == "resource-pane-context-menu-anchor");
        Assert.True(menu.Instance.Open);
        Assert.Equal("cache", menu.Instance.Items[0].Text);
        Assert.True(menu.Instance.Items[0].IsHeader);
        Assert.Equal(expected, menu.Instance.Items.Skip(1).Select(item => (item.Text, item.IsDivider, item.IsDisabled)));
        Assert.Equal([("enabled", readOnly), ("disabled", true)], menu.Instance.Items
            .Where(item => item.Text is "enabled" or "disabled" or "hidden").Select(item => (item.Text, item.IsDisabled)));
        Assert.Equal(["api", "cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal(originalUrl, navigation.Uri);

        if (!readOnly)
        {
            await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Text == "enabled").OnClick!);
            cut.WaitForAssertion(() => Assert.Equal([("cache", "enabled")], executed));
        }
    }

    [Fact]
    public async Task FullCanvas_ReusesFilteredGroupedPaneAndCollapsesOnNavigation()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, client);
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "Tags") : (false, null);
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api", "Backend");
        tags.AddTag("api", "Public");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/cache");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var search = cut.FindComponent<FluentTextInput>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("api"));
        var groups = cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key")).ToArray();
        var rows = cut.FindAll(".resource-row").Select(r => r.GetAttribute("data-resource-name")).ToArray();
        var url = navigation.Uri;

        ResourceSetupHelpers.ToggleResourceList(cut);

        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal("true", cut.Find(".resource-content").GetAttribute("aria-hidden"));
        Assert.Equal(url, navigation.Uri);
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal(groups, cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key")));
        Assert.Equal(rows, cut.FindAll(".resource-row").Select(r => r.GetAttribute("data-resource-name")));
        Assert.Equal(1, client.ResourceSubscriptionCount);
        Assert.Equal("api", search.Instance.Value);

        cut.Find(".resource-row:not(.resource-row-all) .resource-row-link").Click();
        cut.Render();

        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        await cut.InvokeAsync(() => cut.Instance.CompletePaneSelectionReveal(navigation.Uri));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        Assert.Equal("api", search.Instance.Value);
        Assert.Equal(groups, cut.FindAll(".resource-tag-group").Select(g => g.GetAttribute("data-resource-group-key")));
        Assert.Equal(1, client.ResourceSubscriptionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCanvas_TabNavigationOpensContentBeforeDestinationRenders(bool initiallyCollapsed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneCollapsed ? (true, initiallyCollapsed) : (false, null);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/consolelogs/resource/api");
        var header = Render<SectionOutlet>(p => p.Add(p => p.SectionName, MainLayout.HeaderContextSectionName));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.InvokeAsync(() => cut.Instance.SetPaneWidthAsync(440));
        ResourceSetupHelpers.ToggleResourceList(cut);
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        var traces = header.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Traces").Instance;

        await header.InvokeAsync(() => header.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(traces));

        Assert.Equal("http://localhost/traces/resource/api", navigation.Uri);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-collapsed"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal("--resource-pane-expanded-width: 440px", cut.Find(".resources-layout").GetAttribute("style"));
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        Assert.Empty(JSInterop.Invocations["startPaneSelectionReveal"]);
        cut.Render();
        Assert.Equal(ResourceTab.Traces, cut.Instance.CurrentTab);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
    }

    [Theory]
    [InlineData("resource-tabs-overflow-button", "tab-Traces-menu-item", "/traces/resource/api")]
    [InlineData("resource-tabs-overflow-button", "tab-Console-menu-item", "/consolelogs/resource/api")]
    [InlineData("resource-tabs-compact-button", "tab-Traces-menu-item", "/traces/resource/api")]
    [InlineData("resource-tabs-compact-button", "tab-Console-menu-item", "/consolelogs/resource/api")]
    public async Task FullCanvas_TabMenuNavigationOpensContentBeforeDestinationRenders(string buttonClass, string menuItemId, string expectedPath)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/consolelogs/resource/api");
        var header = Render<SectionOutlet>(p => p.Add(p => p.SectionName, MainLayout.HeaderContextSectionName));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        await cut.InvokeAsync(() => cut.Instance.SetOverflowTabs(["tab-Traces", "tab-Console"]));
        var menuItem = header.FindComponents<AspireMenuButton>()
            .Single(b => b.Instance.ButtonClass == buttonClass)
            .Instance.ItemsProvider().Single(item => item.Id == menuItemId);

        await header.InvokeAsync(menuItem.OnClick!);

        Assert.Equal(expectedPath, "/" + navigation.ToBaseRelativePath(navigation.Uri));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-collapsed"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        Assert.Empty(JSInterop.Invocations["startPaneSelectionReveal"]);
    }

    [Fact]
    public void FullCanvas_ViewportChangeExitsToNormalDrawerAndDoesNotReenter()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop,
            new TestDashboardClient(isEnabled: true, initialResources: [ModelTestHelpers.CreateResource("cache")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/cache");
        var cut = Render<CascadingValue<ViewportInformation>>(builder =>
            builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        var layout = cut.FindComponent<ResourcesLayout>().Instance;
        cut.WaitForAssertion(() => Assert.True(layout.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        cut.Render(builder => builder.Add(p => p.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Same(layout, cut.FindComponent<ResourcesLayout>().Instance);
        Assert.Empty(cut.FindAll(".resource-pane-canvas-toggle,.pane-full-canvas,.pane-selection-reveal,.pane-motion-enabled"));
        Assert.True(cut.Find(".resource-pane").HasAttribute("hidden"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Equal(["cache"], layout.SelectedResourceNames);
        cut.Find(".resource-pane-open-button").Click();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.Single(cut.FindAll($"[aria-label='{Resources.Layout.ResourcePaneClose}']"));

        cut.Render(builder => builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Empty(cut.FindAll(".resource-pane-canvas-toggle"));
        Assert.Empty(cut.FindAll(".pane-full-canvas"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("navigate")]
    [InlineData("reverse")]
    [InlineData("resize")]
    public async Task FullCanvas_SelectionRevealOwnsOnlyItsCurrentNavigation(string interruption)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/cache");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.Empty(JSInterop.Invocations["startPaneSelectionReveal"]);
        ResourceSetupHelpers.ToggleResourceList(cut);
        var fitCalls = JSInterop.Invocations["fitPaneToActions"].Count;
        cut.Find("[data-resource-name='api'] .resource-row-link").Click();
        cut.Render();
        var target = navigation.Uri;
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-selection-reveal"));
        Assert.Equal("true", cut.Find(".resource-content").GetAttribute("aria-hidden"));
        Assert.Single(JSInterop.Invocations["startPaneSelectionReveal"]);
        Assert.Equal(fitCalls, JSInterop.Invocations["fitPaneToActions"].Count);

        switch (interruption)
        {
            case "navigate":
                navigation.NavigateTo("/resources/cache");
                cut.Render();
                break;
            case "reverse":
                ResourceSetupHelpers.ToggleResourceList(cut);
                break;
            case "resize":
                await cut.InvokeAsync(() => cut.Instance.SetPaneWidthAsync(440));
                break;
        }

        await cut.InvokeAsync(() => cut.Instance.CompletePaneSelectionReveal(target));
        await cut.InvokeAsync(() => cut.Instance.CompletePaneSelectionReveal(target));
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-selection-reveal"));
            Assert.Equal(interruption == "reverse", cut.Find(".resource-content").HasAttribute("inert"));
            Assert.Equal(interruption == "complete" ? 1 : 0, JSInterop.Invocations["focusPaneSelectionReveal"].Count);
            Assert.Equal(interruption == "navigate" ? ["cache"] : ["api"], cut.Instance.SelectedResourceNames);
            Assert.Equal(fitCalls, JSInterop.Invocations["fitPaneToActions"].Count);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedResource_StatusRemainsInFooterAndFollowsUpdates(bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var changes = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: KnownResourceTypes.Project)],
            resourceChannelProvider: () => changes));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            var footer = cut.Find(".resource-workspace-foot");
            Assert.Equal(cut.Find(".resources-layout"), footer.ParentElement);
            Assert.Equal("Running", footer.QuerySelector(".state-pill")!.TextContent.Trim());
            Assert.Equal("Project", footer.QuerySelector(".resource-status-type")!.TextContent);
            Assert.Empty(cut.FindAll(".resource-header .resource-header-meta"));
        });

        await changes.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert,
            ModelTestHelpers.CreateResource("api", state: KnownResourceState.Exited, resourceType: KnownResourceTypes.Project))]);

        cut.WaitForAssertion(() => Assert.Equal("Exited", cut.Find(".resource-footer-status .state-pill").TextContent.Trim()));
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public void AppHostTagsParticipateInGroupingWithoutUserTags()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.Tag;
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
        var name = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.Tag;
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

        var menu = await OpenGroupingMenuAsync(cut);
        var otherMode = otherModes.Keys.First();
        await cut.InvokeAsync(menu.Instance.Items.Single(item => item.Id == $"resource-grouping-{otherMode}").OnClick!);
        Assert.False(await cut.InvokeAsync(() => cut.Instance.ReorderGroupAsync(grouping, movedKey, targetKey, after: false)));
        await cut.Find("#resourcePaneGroupingButton").ClickAsync(new MouseEventArgs());
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
    public async Task AppHost_ResourceLinksHaveAccessibleMetadataInsteadOfHoverTooltips(bool collapsed)
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

        if (collapsed)
        {
            await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        }

        Assert.Equal(
            [
                ("cache", "Running; Container (persistent); Health state: Healthy", (string?)null)
            ],
            cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link").Select(link => (
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
        Services.GetRequiredService<NavigationManager>().NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        if (collapsed)
        {
            await cut.Find("#resourcePanePickerButton").ClickAsync(new MouseEventArgs());
        }

        Assert.Equal(
            [
                ("All sources", "1 telemetry source", (string?)null),
                ("api", Resources.Layout.ResourcePaneTelemetryOnly, null)
            ],
            cut.FindAll(".resource-row-link").Select(link => (
                link.GetAttribute("aria-label"), link.GetAttribute("aria-description"), link.GetAttribute("data-tooltip"))));
        var sourceRow = cut.Find(".resource-row-kind-telemetry");
        Assert.Equal("api", sourceRow.QuerySelector(".resource-row-name")!.TextContent);
        Assert.Equal("Telemetry sources", sourceRow.QuerySelector(".resource-row-kind-label")!.TextContent);
    }

    // The pane used to have a toggle for each kind of item, stored as a list such as "Resources,Parameters". Telemetry
    // sources, off by default then, stay hidden for someone who never turned them on. Without a stored value they're
    // listed, like everything the filter starts with.
    [Theory]
    [InlineData(null, true)]
    [InlineData("Resources,Parameters", false)]
    [InlineData("Resources,Parameters,TelemetrySources", true)]
    public async Task AppHost_TelemetryOnlyResourcesAreListedUnlessFilteredOut(string? storedKinds, bool initiallyListed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        await AddTelemetryAsync("browser");
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneKinds && storedKinds is not null ? (true, storedKinds) : (false, null);
        var stored = new Dictionary<string, object?>();
        storage.OnSetUnprotectedAsync = (key, value) => stored[key] = value;

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        string[] listed = initiallyListed ? ["cache", "browser"] : ["cache"];
        cut.WaitForAssertion(() => Assert.Equal(listed, cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link").Select(link => link.GetAttribute("aria-label"))));
        Assert.Equal(initiallyListed ? "1 resource, 1 telemetry source" : "1 resource", cut.Find(".resource-pane-foot > span").GetAttribute("aria-label"));

        // The filter's "Telemetry sources" option changes it, and the choice is remembered.
        var filter = OpenFilter(cut);
        var option = GetFilterCheckboxes(filter, "types").Single(box => box.Instance.Label == "Telemetry sources");
        Assert.Equal(initiallyListed, option.Instance.Value);
        await cut.InvokeAsync(() => option.Instance.ValueChanged.InvokeAsync(!initiallyListed));

        string[] toggled = initiallyListed ? ["cache"] : ["cache", "browser"];
        cut.WaitForAssertion(() => Assert.Equal(toggled, cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link").Select(link => link.GetAttribute("aria-label"))));
        Assert.Equal(initiallyListed ? string.Empty : "TelemetrySources", stored[BrowserStorageKeys.ResourcePaneKinds]);
    }

    [Fact]
    public async Task AppHost_CountsResourcesAndParametersSeparately()
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
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        // In the two-line densities a parameter row has no type: its kind is clear from its icon and section.
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Comfortable));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["api", "worker"], cut.FindAll(".resource-row-kind-resource").Select(row => row.GetAttribute("data-resource-name")));
            var parameter = cut.Find(".resource-row-kind-parameter");
            Assert.Equal("parameter", parameter.GetAttribute("data-resource-name"));
            Assert.Empty(parameter.QuerySelectorAll(".resource-row-type"));
            Assert.EndsWith("Parameter", parameter.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
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

        Assert.Empty(cut.FindAll("[data-resource-name='root'] .resource-row-child-count"));
        cut.Find("[data-resource-name='root'] .resource-row-toggle").Click();
        Assert.Equal(["root", "worker"], GetNames());
        // A collapsed parent says how many rows it hides.
        var childCount = cut.Find("[data-resource-name='root'] .resource-row-child-count");
        Assert.Equal("2", childCount.TextContent);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.Layout.ResourcePaneNestedCount, 2), childCount.GetAttribute("data-tooltip"));
        Assert.True(await cut.InvokeAsync(() => cut.Instance.ReorderResourceAsync("worker", "root", after: false)));
        cut.Find("[data-resource-name='root'] .resource-row-toggle").Click();
        Assert.Equal(["worker", "root", "a", "b"], GetNames());
        Assert.Empty(cut.FindAll("[data-resource-name='root'] .resource-row-child-count"));

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
    public async Task CompactDensity_ShowsOneLineWithStateAfterNameAndTypeColumn()
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

        // The list starts compact.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-pane-list.density-compact")));
        AssertCompactRows();

        // The setting applies right away, without reloading the page.
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();
        await cut.InvokeAsync(() => preferences.SetDensityAsync(ResourceListDensity.Comfortable));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name] .resource-row-sub").Count));
        Assert.Empty(cut.FindAll(".resource-row-meta"));
        await cut.InvokeAsync(() => preferences.SetDensityAsync(ResourceListDensity.Compact));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-pane-list.density-compact")));
        AssertCompactRows();
        Assert.Empty(cut.FindAll("[data-resource-name='api'] .persistent-container-badge"));
        Assert.Single(cut.FindAll("[data-resource-name='cache'] .persistent-container-badge"));
        Assert.Equal("Running; Project; Health state: Healthy", cut.Find("[data-resource-name='api'] .resource-row-link").GetAttribute("aria-description"));

        // The expanded resource list keeps the same single-line identity next to its metadata columns.
        ResourceSetupHelpers.ToggleResourceList(cut);
        AssertCompactRows();

        // One line per resource: the name with its state dot right after it, then the type column, with no second
        // line of state text. Both views put the dot in the name's text, so it describes the resource, not its type.
        void AssertCompactRows()
        {
            Assert.Empty(cut.FindAll("[data-resource-name] .resource-row-sub"));
            Assert.Equal("Project", Assert.Single(cut.FindAll("[data-resource-name='api'] .resource-row-meta .resource-row-type")).TextContent);
            var cacheType = Assert.Single(cut.FindAll("[data-resource-name='cache'] .resource-row-meta .resource-row-type"));
            Assert.Equal("Container (persistent)", cacheType.TextContent);
            Assert.True(cacheType.ClassList.Contains("resource-row-type-persistent"));
            Assert.Equal(["Running", "Running"],
                cut.FindAll("[data-resource-name] .resource-row-meta-state").Select(state => state.GetAttribute("data-tooltip")));
            Assert.Equal(["resource-row-text", "resource-row-text"],
                cut.FindAll("[data-resource-name] .resource-row-meta-state").Select(state => state.ParentElement!.ClassName));
        }
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
    public void Tabs_KeepOverviewForAResourceThatIsNotFound()
    {
        // A replica that was replaced leaves its pages' URLs behind. Its overview says it wasn't found, so the tab stays
        // as the way back to it from the resource's console logs. The tabs render in this component below the desktop
        // breakpoint (on desktop they're in the app header).
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ConsoleLogsUrl("api-replaced"));

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Equal(
            ["tab-Overview", "tab-Console", "tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
            cut.FindComponents<FluentTab>().Where(t => t.Instance.Visible && t.Instance.Id != "resource-tab-none").Select(t => t.Instance.Id)));
    }

    [Fact]
    public void Header_MissingReplica_LinksToTheReplicasThatReplacedIt()
    {
        // Restarting the app replaces a replica ("api-old") with ones under new suffixes. The page left open on the old
        // name says it wasn't found and links to the replicas of the same resource that run now, on the same tab.
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource(resourceName: "api-new1", displayName: "api"),
                ModelTestHelpers.CreateResource(resourceName: "api-new2", displayName: "api"),
                // Not a replica of "api": its name is its display name.
                ModelTestHelpers.CreateResource(resourceName: "api-gateway", displayName: "api-gateway")
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ConsoleLogsUrl("api-old"));

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find(".resource-header-meta a");
            Assert.Equal("Show api", link.TextContent);
            Assert.Equal(ResourcesLayout.GetTabUrl(ResourceTab.Console, ["api-new1", "api-new2"]), link.GetAttribute("href"));
        });
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

        // "resource-tab-none" is the tab that's active while no view is shown selected. It's never displayed.
        cut.WaitForAssertion(() => Assert.Equal(
            ["tab-Overview", "tab-Console", "tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
            cut.FindComponents<FluentTab>().Where(t => t.Instance.Visible && t.Instance.Id != "resource-tab-none").Select(t => t.Instance.Id)));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowActions_PromoteAvailableLifecycleCommandsThroughExistingExecutor(bool readOnly)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var executed = new List<string>();
        static CommandViewModel Command(string name, CommandViewModelState state, bool highlighted) =>
            new(name, state, name, "", confirmationMessage: "", argumentInputs: [], isHighlighted: highlighted, iconName: "", iconVariant: IconVariant.Regular);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true, isReadOnly: readOnly,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, commands:
            [
                Command(CommandViewModel.StartCommand, CommandViewModelState.Hidden, false),
                Command(CommandViewModel.StopCommand, CommandViewModelState.Enabled, true),
                Command(CommandViewModel.RestartCommand, CommandViewModelState.Enabled, false)
            ])],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            executeResourceCommand: (_, _, command, _, _) =>
            {
                executed.Add(command.Name);
                return Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Succeeded });
            }));
        Render<FluentToastProvider>();
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-row-highlighted")));
        Assert.Equal("stop api", cut.Find(".resource-row-highlighted").GetAttribute("aria-label"));
        ResourceSetupHelpers.ToggleResourceList(cut);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-row-highlighted").Count));
        var buttons = cut.FindAll(".resource-row-highlighted");
        Assert.Equal(["stop api", "restart api"], buttons.Select(b => b.GetAttribute("aria-label")));
        Assert.All(buttons, b => Assert.Equal(readOnly, b.HasAttribute("disabled")));
        if (!readOnly)
        {
            await cut.InvokeAsync(() => buttons[1].Click());
            cut.WaitForAssertion(() => Assert.Equal(["restart"], executed));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sidebar_PrioritizesBothCustomCommandsWithoutChangingTheirSafety(bool readOnly)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        static CommandViewModel Command(string name, CommandViewModelState state) =>
            new(name, state, name, "", confirmationMessage: "", argumentInputs: [], isHighlighted: true, iconName: "", iconVariant: IconVariant.Regular);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true, isReadOnly: readOnly,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, commands:
            [
                Command(CommandViewModel.StopCommand, CommandViewModelState.Enabled),
                Command("hidden", CommandViewModelState.Hidden),
                Command("deploy", CommandViewModelState.Enabled),
                Command("inspect", CommandViewModelState.Disabled)
            ])], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-row-highlighted").Count));
        Assert.Collection(cut.FindAll(".resource-row-highlighted"),
            button =>
            {
                Assert.Equal("deploy api", button.GetAttribute("aria-label"));
                Assert.Equal(readOnly, button.HasAttribute("disabled"));
            },
            button =>
            {
                Assert.Equal("inspect api", button.GetAttribute("aria-label"));
                Assert.True(button.HasAttribute("disabled"));
            });
        var menu = cut.FindComponents<AspireMenuButton>().Single(b => b.Instance.Title == "Actions for api");
        Assert.Contains(menu.Instance.ItemsProvider(), item => item.Text == "stop");
    }

    [Fact]
    public void Relationships_KeepParentWaitAndReferenceMeaningInRepeatedTagMemberships()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var tagKey = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.Tag;
        var resource = ModelTestHelpers.CreateResource("catalogdb", properties: new()
        {
            [KnownProperties.Resource.ParentName] = new(KnownProperties.Resource.ParentName, Value.ForString("postgres"), false, null, 0, null, false),
            [tagKey] = new(tagKey, Value.ForList([Value.ForString("Data"), Value.ForString("Backend")]), false, null, 0, null, false)
        }, relationships:
        [
            new("migration", KnownRelationshipTypes.WaitFor),
            new("cache", KnownRelationshipTypes.Reference),
            new("catalogdb", KnownRelationshipTypes.Reference),
            new("managed", KnownRelationshipTypes.Manages)
        ]);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [resource, ModelTestHelpers.CreateResource("postgres")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>()).OnGetUnprotectedAsync =
            key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "Tags") : (false, null);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-resource-name='catalogdb']").Count));
        foreach (var membership in cut.FindAll("[data-resource-name='catalogdb']"))
        {
            Assert.Empty(membership.QuerySelectorAll(".resource-row-relationship"));
            Assert.Contains("Child of postgres", membership.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
            Assert.Contains("Depends on migration", membership.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
            Assert.Contains("References cache", membership.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
            Assert.Empty(membership.QuerySelectorAll(".resource-tree-guide, .resource-tree-stem"));
            Assert.Equal("0", membership.QuerySelector(".resource-row-link")!.GetAttribute("data-resource-depth"));
        }
        ResourceSetupHelpers.ToggleResourceList(cut);
        Assert.All(cut.FindAll("[data-resource-name='catalogdb']"), membership =>
        {
            Assert.Empty(membership.QuerySelectorAll(".resource-relationship-summary, .resource-row-dependencies, .resource-row-references"));
            Assert.Single(membership.QuerySelectorAll(".resource-row-source"));
            Assert.Contains("Depends on migration", membership.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
            Assert.Contains("References cache", membership.QuerySelector(".resource-row-link")!.GetAttribute("aria-description"));
        });
    }

    [Fact]
    public void Hierarchy_GuidesContinueThroughSiblingsAndTerminateAtLastDescendant()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        static ResourceViewModel Child(string name, string parent) => ModelTestHelpers.CreateResource(name, properties: new()
        {
            [KnownProperties.Resource.ParentName] = new(KnownProperties.Resource.ParentName, Value.ForString(parent), false, null, 0, null, false)
        });
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("parent"), Child("first", "parent"),
                Child("nested", "first"), Child("last", "parent")
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var first = cut.Find("[data-resource-name='first']");
        Assert.True(first.QuerySelector(".resource-tree-guide")!.ClassList.Contains("continuing"));
        Assert.Single(first.QuerySelectorAll(".resource-tree-stem"));
        var nested = cut.Find("[data-resource-name='nested']");
        Assert.Collection(nested.QuerySelectorAll(".resource-tree-guide"),
            guide =>
            {
                Assert.True(guide.ClassList.Contains("continuing"));
                Assert.False(guide.ClassList.Contains("branch"));
            },
            guide =>
            {
                Assert.False(guide.ClassList.Contains("continuing"));
                Assert.True(guide.ClassList.Contains("branch"));
            });
        var last = cut.Find("[data-resource-name='last'] .resource-tree-guide");
        Assert.True(last.ClassList.Contains("branch"));
        Assert.False(last.ClassList.Contains("continuing"));
        first.QuerySelector(".resource-row-toggle")!.Click();
        Assert.Empty(cut.FindAll("[data-resource-name='nested']"));
        Assert.Single(cut.FindAll("[data-resource-name='first'] .resource-tree-guide.continuing"));
    }

    [Theory]
    [InlineData(true, ResourceListDensity.Comfortable)]
    [InlineData(true, ResourceListDensity.Compact)]
    [InlineData(false, ResourceListDensity.Comfortable)]
    public async Task RowMenuTerminal_SelectsOnlyItsResourceAndPreservesViewState(bool desktop, ResourceListDensity density)
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

        // The rail has the endpoint, the command and the menu, which has the terminal. A compact sidebar row puts the
        // toggle, here its spacer, after the actions, so the tab order matches the screen. The other densities keep it
        // first.
        var rail = cut.Find("a.resource-row-primary-endpoint").ParentElement!.Children.ToList();
        var spacer = density == ResourceListDensity.Compact ? rail[^1] : rail[0];
        Assert.True(spacer.ClassList.Contains("resource-row-toggle-spacer"));
        Assert.Collection(rail.Where(child => child != spacer),
            endpoint => Assert.True(endpoint.ClassList.Contains("resource-row-action-priority-0")),
            command => Assert.True(command.ClassList.Contains("resource-row-action-priority-1")),
            menu => Assert.True(menu.ClassList.Contains("aspire-menu-button")));

        if (!desktop)
        {
            cut.Find(".resource-pane-open-button").Click();
            Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        }

        // Opening the terminal from the row's menu selects only its resource and keeps the other URL state.
        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.Title == "Actions for shell");
        var terminalItem = Assert.Single(menu.Instance.ItemsProvider(), item => item.Text == "Terminal");
        await cut.InvokeAsync(Assert.IsType<Func<Task>>(terminalItem.OnClick));
        Assert.Equal("http://localhost" + terminalUrl, navigation.Uri);
        cut.Render();
        Assert.Equal(["shell"], cut.Instance.SelectedResourceNames);
        Assert.Equal(ResourceTab.Terminal, cut.Instance.CurrentTab);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
    }

    [Fact]
    public void RowMenuTerminal_IsUnavailableInReadOnlyDashboards()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true, isReadOnly: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("shell")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        var menu = cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.Title == "Actions for shell");
        Assert.All(menu.Instance.ItemsProvider(), item => Assert.NotEqual("Terminal", item.Text));
        Assert.False(cut.Instance.IsTabAvailable(ResourceTab.Terminal));
    }

    // The filters are a popover that stays open while values change. Expands only the given group, so the checkboxes
    // found are that group's: the first is "(All)", the rest are its values in order.
    private static async Task<IRenderedComponent<AspireMenuButton>> OpenGroupingMenuAsync(IRenderedComponent<ResourcesLayout> cut)
    {
        // Click() only queues the event while the renderer is busy, e.g. applying a resource update from the
        // subscription, so await the click before reading the items the menu populates when it opens.
        await cut.Find("#resourcePaneGroupingButton").ClickAsync(new MouseEventArgs());
        return cut.FindComponents<AspireMenuButton>().Single(button => button.Instance.MenuButtonId == "resourcePaneGroupingButton");
    }

    private static IRenderedComponent<ResourcePaneFilter> OpenFilter(IRenderedComponent<ResourcesLayout> cut)
    {
        cut.Find("#resourcePaneFilterButton").Click();
        return cut.FindComponent<ResourcePaneFilter>();
    }

    private static List<IRenderedComponent<FluentCheckbox>> GetFilterCheckboxes(IRenderedComponent<ResourcePaneFilter> filter, string groupId)
    {
        var target = groupId switch { "types" => 0, "states" => 1, "health" => 2, _ => 3 };
        // The "Show" section at the top lists kinds, not values, and doesn't collapse.
        var count = filter.FindAll("button.resource-filter-group-header").Count;
        for (var i = 0; i < count; i++)
        {
            var header = filter.FindAll("button.resource-filter-group-header")[i];
            if ((header.GetAttribute("aria-expanded") == "true") != (i == target))
            {
                header.Click();
            }
        }

        return filter.FindComponents<FluentCheckbox>().Where(box => box.Instance.Class?.Contains("resource-filter-kind", StringComparison.Ordinal) != true).ToList();
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
            boxes.Skip(1).Select(box => box.Instance.Label!).Zip(filter.FindAll(".resource-filter-group:not(.resource-filter-kinds) .resource-filter-option .resource-filter-count").Select(count => count.TextContent.Trim())));
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
            boxes.Select((box, index) => (box.Instance.Label!, index == 0 ? null : filter.FindAll(".resource-filter-group:not(.resource-filter-kinds) .resource-filter-option .resource-filter-count")[index - 1].TextContent.Trim())));

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
    public async Task Filter_ListsTelemetrySourcesAfterTheResourceTypes()
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
        cut.WaitForAssertion(() => Assert.Equal(["api", "parameter", "browser"], GetRowNames()));
        Assert.Equal(["api", "browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        // Telemetry sources aren't a resource type, so their option comes after the types, with its own count.
        var filter = OpenFilter(cut);
        var types = GetFilterCheckboxes(filter, "types");
        Assert.Equal(["(All)", KnownResourceTypes.Container, KnownResourceTypes.Parameter, "Telemetry sources"], types.Select(box => box.Instance.Label));
        Assert.Equal(["1", "1", "1"], filter.FindAll(".resource-filter-count").Select(count => count.TextContent));
        Assert.True(cut.Find(".resource-filter-reset").HasAttribute("disabled"));

        // Hiding them takes them out of the list and out of what "all resources" covers.
        await cut.InvokeAsync(() => types.Single(box => box.Instance.Label == "Telemetry sources").Instance.ValueChanged.InvokeAsync(false));
        cut.WaitForAssertion(() => Assert.Equal(["api", "parameter"], GetRowNames()));
        Assert.Equal(["api"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
        Assert.Equal("2/3", filter.Find(".resource-filter-group-summary").TextContent);
        Assert.False(cut.Find(".resource-filter-reset").HasAttribute("disabled"));

        // Parameters are a resource type, so the types filter them like the other resources.
        types = GetFilterCheckboxes(filter, "types");
        await cut.InvokeAsync(() => types.Single(box => box.Instance.Label == KnownResourceTypes.Parameter).Instance.ValueChanged.InvokeAsync(false));
        cut.WaitForAssertion(() => Assert.Equal(["api"], GetRowNames()));

        // Reset shows everything again, telemetry sources included.
        cut.Find(".resource-filter-reset").Click();
        cut.WaitForAssertion(() => Assert.Equal(["api", "parameter", "browser"], GetRowNames()));
        Assert.Equal(["api", "browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        IEnumerable<string> GetRowNames() => cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-name").Select(name => name.TextContent.Trim());
    }

    [Fact]
    public async Task Landing_WithoutRememberedSelection_ShowsAllResourcesInTheFullCanvasListWithNoTabSelected()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ResourceListUrl());

        var header = Render<SectionOutlet>(p => p.Add(p => p.SectionName, MainLayout.HeaderContextSectionName));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        // The dashboard opens on the list of all resources, filling the page, with no view open yet.
        cut.WaitForAssertion(() => Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas")));
        Assert.Equal(2, cut.FindAll("[data-resource-name]").Count);
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        var allRow = cut.Find(".resource-row-all");
        Assert.Equal("All resources", allRow.QuerySelector(".resource-row-name")!.TextContent.Trim());
        Assert.True(allRow.ClassList.Contains("selected"));
        Assert.Equal("page", allRow.QuerySelector(".resource-row-link")!.GetAttribute("aria-current"));
        Assert.Empty(cut.FindAll(".resource-row.selected[data-resource-name]"));
        // No tab is shown selected, but the overview is kept as the tab to open for a selection.
        Assert.Equal("resource-tab-none", header.FindComponent<FluentTabs>().Instance.ActiveTabId);
        Assert.Equal(ResourceTab.Overview, cut.Instance.CurrentTab);
        Assert.Equal(DashboardUrls.ResourceOverviewUrl("api"), cut.Find("[data-resource-name='api'] .resource-row-link").GetAttribute("href"));

        // FluentTabs reports a change of the current tab when it syncs with the active tab too, for example right after
        // the content closes with that tab focused, so such a change doesn't open the content.
        var overview = header.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Overview").Instance;
        await header.InvokeAsync(() => header.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(overview));
        cut.Render();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        // Clicking it, which the layout's script reports, opens it for all resources although the URL doesn't change.
        await cut.InvokeAsync(() => cut.Instance.OpenTabFromFullCanvasAsync("tab-Overview"));
        cut.Render();
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        Assert.Equal("tab-Overview", header.FindComponent<FluentTabs>().Instance.ActiveTabId);
        Assert.Equal("All resources", cut.Find(".resource-header-title").TextContent.Trim());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Landing_ShowsWhatTheBrowserTabLastShowed(bool contentOpen)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var stored = new Dictionary<string, object?> { [BrowserStorageKeys.ResourceContentOpen] = contentOpen };
        var writes = new List<(string Key, object? Value)>();
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => stored.TryGetValue(key, out var value) ? (true, value) : (false, null),
            OnSetAsync = (key, value) => writes.Add((key, value))
        };
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ISessionStorage>(sessionStorage));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());

        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        // The overview of all resources has the dashboard's landing URL, so reloading it shows the full-page list only
        // when the list was what the browser tab showed. A link such as /graph stores the content as open too.
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        cut.WaitForAssertion(() => Assert.Equal(!contentOpen, cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas")));
        Assert.Equal(ResourceTab.Overview, cut.Instance.CurrentTab);
        // What was read is already stored, so only the full-page list the dashboard lands on is written.
        object?[] expectedWrites = contentOpen ? [] : [false];
        cut.WaitForAssertion(() => Assert.Equal(expectedWrites,
            writes.Where(write => write.Key == BrowserStorageKeys.ResourceContentOpen).Select(write => write.Value)));
    }

    [Fact]
    public async Task Landing_RemembersWhetherTheContentIsOpenForTheBrowserTab()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var writes = new List<object?>();
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                if (key == BrowserStorageKeys.ResourceContentOpen)
                {
                    writes.Add(value);
                }
            }
        };
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ISessionStorage>(sessionStorage));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));

        // Nothing was open in the tab, so the dashboard opens on the full-page list, and remembers it. The content shown
        // before the landing decided isn't remembered as open.
        cut.WaitForAssertion(() => Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas")));
        cut.WaitForAssertion(() => Assert.Equal([false], writes));

        await cut.InvokeAsync(() => cut.Instance.OpenTabFromFullCanvasAsync("tab-Overview"));
        cut.WaitForAssertion(() => Assert.Equal([false, true], writes));
    }

    [Theory]
    [InlineData("/resources/api")]
    [InlineData("/resources/api?terminal=true")]
    [InlineData("/terminals/resource/api")]
    public async Task AllResourcesRow_AlwaysSelectsEverything(string initialUrl)
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
        var allRow = cut.Find(".resource-row-all");
        Assert.False(allRow.ClassList.Contains("selected"));
        Assert.Null(allRow.QuerySelector(".resource-row-link")!.GetAttribute("aria-current"));

        await cut.InvokeAsync(() => allRow.QuerySelector(".resource-row-link")!.Click());

        // The row navigates to the resources without a selection. The router then gives the layout the new location.
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        cut.Render();
        cut.WaitForAssertion(() => Assert.Equal("All resources", cut.Find(".resource-header-title").TextContent.Trim()));
        await cut.InvokeAsync(() => cut.Find(".resource-row-all .resource-row-link").Click());
        Assert.Equal("resources", navigationManager.ToBaseRelativePath(navigationManager.Uri));
        cut.Render();

        Assert.True(cut.Find(".resource-row-all").ClassList.Contains("selected"));
        Assert.Empty(cut.FindAll(".resource-row.selected[data-resource-name]"));
        Assert.Empty(cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link[aria-current]"));
        Assert.Empty(cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public async Task AllResourcesRow_IsTheOnlySelectedRowAcrossResourceKinds()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api"),
                ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)
            ],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        await AddTelemetryAsync("browser");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".resource-row:not(.resource-row-all)").Count));

        await cut.InvokeAsync(() => cut.Find(".resource-row-all .resource-row-link").Click());
        cut.Render();

        Assert.True(cut.Find(".resource-row-all").ClassList.Contains("selected"));
        Assert.Empty(cut.FindAll(".resource-row.selected:not(.resource-row-all)"));
        Assert.Empty(cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link[aria-current]"));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        await cut.InvokeAsync(() => cut.Find(".resource-row-kind-telemetry .resource-row-link").Click());
        cut.Render();

        Assert.False(cut.Find(".resource-row-all").ClassList.Contains("selected"));
        Assert.Equal(["browser"], cut.Instance.SelectedResourceNames);
        Assert.Equal("page", cut.Find(".resource-row-kind-telemetry.selected .resource-row-link").GetAttribute("aria-current"));
        Assert.Single(cut.FindAll(".resource-row.selected"));
    }

    [Theory]
    [InlineData("Metrics", "/metrics/resource/cache?resource=cache&resource=api")]
    [InlineData("Traces", "/traces?resource=cache&resource=api")]
    [InlineData("StructuredLogs", "/structuredlogs?resource=cache&resource=api")]
    public async Task Telemetry_ChangingTheSelectionStaysOnTheCurrentTabEvenWithoutTelemetry(string tabName, string multiSelectionUrl)
    {
        var tab = System.Enum.Parse<ResourceTab>(tabName);
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(ResourcesLayout.GetTabUrl(tab, ["api"]));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        // Neither resource sends telemetry, which would otherwise send a selection to the overview.
        var cacheLink = cut.Find("[data-resource-name='cache'] .resource-row-link");
        Assert.Equal(ResourcesLayout.GetTabUrl(tab, ["cache"]), cacheLink.GetAttribute("href"));
        await cut.InvokeAsync(() => cacheLink.Click());
        Assert.Equal(ResourcesLayout.GetTabUrl(tab, ["cache"]), "/" + navigation.ToBaseRelativePath(navigation.Uri));
        cut.Render();
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);

        await cut.InvokeAsync(() => cut.Find("[data-resource-name='api'] .resource-row-link")
            .Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { CtrlKey = true }));
        Assert.Equal(multiSelectionUrl, "/" + navigation.ToBaseRelativePath(navigation.Uri));
        cut.Render();
        Assert.Equal(["cache", "api"], cut.Instance.SelectedResourceNames);

        await cut.InvokeAsync(() => cut.Find(".resource-row-all .resource-row-link").Click());
        Assert.Equal(ResourcesLayout.GetTabUrl(tab, []), "/" + navigation.ToBaseRelativePath(navigation.Uri));
        cut.Render();
        Assert.Empty(cut.Instance.SelectedResourceNames);
    }

    [Theory]
    [InlineData("Metrics")]
    [InlineData("Traces")]
    [InlineData("StructuredLogs")]
    public async Task Telemetry_SelectingOnlyAParameterNavigatesToOverview(string tabName)
    {
        var tab = System.Enum.Parse<ResourceTab>(tabName);
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter)],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(ResourcesLayout.GetTabUrl(tab, ["api"]));
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        var parameterLink = cut.Find("[data-resource-name='parameter'] .resource-row-link");
        Assert.Equal(DashboardUrls.ResourceOverviewUrl("parameter"), parameterLink.GetAttribute("href"));
        await cut.InvokeAsync(() => parameterLink.Click());

        Assert.Equal("http://localhost/resources/parameter", navigation.Uri);
    }

    [Fact]
    public void Traces_SelectedResourceWithoutTelemetryShowsEmptyState()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        Services.AddTransient<TracesViewModel>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        FluentUISetupHelpers.SetupFluentList(this);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.TracesUrl("cache"));

        var cut = Render<ResourcesLayout>(builder => builder
            .AddCascadingValue(viewport)
            .Add(p => p.Body, body =>
            {
                body.OpenComponent<Components.Pages.Traces>(0);
                body.AddComponentParameter(1, nameof(Components.Pages.Traces.ResourceName), "cache");
                body.CloseComponent();
            }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
            Assert.Equal(Resources.Traces.TracesNoTraces, cut.Find(".telemetry-empty-content > div").TextContent.Trim());
            Assert.Equal("http://localhost/traces/resource/cache", navigation.Uri);
        });
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
        Assert.Equal(resourceServiceEnabled ? ResourcesLayout.ResourceListHomeHistoryEntryState : null, navigationManager.HistoryEntryState);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HomeNavigation_OpensFullResourceListOnDesktop(bool isDesktop, bool expectFullCanvas)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => channel));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources", new NavigationOptions { HistoryEntryState = ResourcesLayout.ResourceListHomeHistoryEntryState });

        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Equal(expectFullCanvas, cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Equal("http://localhost/resources", navigation.Uri);
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
            var menu = await OpenGroupingMenuAsync(cut);
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
    [InlineData("Session", ResourceListDensity.Comfortable, "Container")]
    [InlineData("Session", ResourceListDensity.Compact, "Container")]
    [InlineData(null, ResourceListDensity.Comfortable, "Container")]
    [InlineData(null, ResourceListDensity.Compact, "Container")]
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

        // Only compact sidebar rows have a type column; the other densities show the start time instead.
        Assert.Equal(density is ResourceListDensity.Compact ? [expectedType] : [],
            cut.FindAll("[data-resource-name='cache'] .resource-row-type").Select(type => type.TextContent));
        Assert.Equal($"Running; {expectedType}; Health state: Healthy", cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("aria-description"));
        ResourceSetupHelpers.ToggleResourceList(cut);
        var type = cut.Find("[data-resource-name='cache'] .resource-row-type");
        Assert.Equal(expectedType, type.TextContent);
        Assert.Equal(expectedType, type.GetAttribute("data-tooltip"));
        Assert.Equal($"Running; {expectedType}; Health state: Healthy", cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("aria-description"));
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

        var menu = await OpenGroupingMenuAsync(cut);
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

        var menu = await OpenGroupingMenuAsync(cut);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppHost_LegacyTagsNavigationHandlesDisconnectedPersistence(bool grouping)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        var disconnectedKey = grouping ? BrowserStorageKeys.ResourcePaneGrouping : BrowserStorageKeys.ResourcePaneMode;
        var attempted = false;
        storage.OnSetUnprotectedAsync = (key, _) =>
        {
            if (key == disconnectedKey)
            {
                attempted = true;
                throw new JSDisconnectedException("The circuit disconnected during navigation.");
            }
        };
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.TagsUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            Assert.True(attempted);
            Assert.True(cut.Instance.IsLoaded);
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
            // Telemetry sources are listed by default, so "all resources" covers browser too.
            Assert.Equal(["api", "browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
            Assert.Equal([DashboardUrls.StructuredLogsUrl("api"), DashboardUrls.StructuredLogsUrl("browser")],
                cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link").Select(link => link.GetAttribute("href")));
        });

        navigation.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal(["api"], cut.Instance.GetAllResourceNames());
            Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
            Assert.Equal(["/structuredlogs/resource/api", "/structuredlogs/resource/browser"],
                cut.FindAll(".resource-row:not(.resource-row-all) .resource-row-link").Select(link => link.GetAttribute("href")));
        });
        navigation.NavigateTo(DashboardUrls.AddTelemetryPane(DashboardUrls.TracesUrl("browser")));
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal("/traces/resource/browser", cut.Instance.GetTabUrl(ResourceTab.Traces));
            Assert.Equal("/metrics/resource/browser?duration=15&pane=telemetry",
                cut.Instance.AddSelectionToUrl("/metrics/resource/browser?duration=15"));
            Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
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
            Assert.Single(cut.FindAll(".resource-row-all"));
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
