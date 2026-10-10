// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
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
    [Theory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces/detail/abc")]
    [InlineData("/metrics")]
    public async Task Standalone_RendersTelemetryResourcesAndTabs(string path)
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
            Assert.Equal(["tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
                cut.FindComponents<FluentTab>().Where(t => t.Instance.Visible).Select(t => t.Instance.Id));
            Assert.Equal(3, cut.FindAll(".resource-row").Count);
            Assert.Equal("2 sources", cut.Find(".resource-pane-foot").TextContent.Trim());
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
            Assert.Equal("1 source", cut.Find(".resource-pane-foot").TextContent.Trim());
            Assert.Equal(["api", "worker"], cut.Instance.GetAllResourceNames());
        });
    }

    [Fact]
    public async Task Standalone_SelectingResourcesAndTabsPreservesSelection()
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
        await cut.InvokeAsync(() => cut.FindComponent<FluentTabs>().Instance.ActiveTabChanged.InvokeAsync(
            cut.FindComponents<FluentTab>().Single(t => t.Instance.Id == "tab-Traces").Instance));
        Assert.Equal("http://localhost/traces?resource=api&resource=worker", navigation.Uri);
    }

    [Fact]
    public async Task Standalone_ChangingViewportPreservesTabsAndRoute()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop, new TestDashboardClient(isEnabled: false));
        await AddTelemetryAsync("api");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(DashboardUrls.StructuredLogsUrl("api"));
        var cut = Render<CascadingValue<ViewportInformation>>(builder =>
            builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        var tabs = cut.FindComponent<FluentTabs>().Instance;

        cut.Render(builder => builder.Add(p => p.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Same(tabs, cut.FindComponent<FluentTabs>().Instance);
        Assert.Equal("http://localhost/structuredlogs/resource/api", navigation.Uri);
        cut.Find(".resource-pane-open-button").Click();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));

        cut.Render(builder => builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Same(tabs, cut.FindComponent<FluentTabs>().Instance);
        Assert.Equal("http://localhost/structuredlogs/resource/api", navigation.Uri);
    }

    [Fact]
    public async Task AppHost_TelemetryPaneSeparatesExternalSourcesAndPreservesNavigationContext()
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
            Assert.Equal([DashboardUrls.StructuredLogsUrl(), DashboardUrls.StructuredLogsUrl("api")],
                cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("href")));
        });

        navigation.NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        cut.Render();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.IsTelemetryPane);
            Assert.Equal(["browser"], cut.Instance.GetAllResourceNames());
            Assert.Equal(["browser"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));
            Assert.Equal(["tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
                cut.FindComponents<FluentTab>().Where(tab => tab.Instance.Visible).Select(tab => tab.Instance.Id));
            Assert.Equal(["/structuredlogs?pane=telemetry", "/structuredlogs/resource/browser?pane=telemetry"],
                cut.FindAll(".resource-row-link").Select(link => link.GetAttribute("href")));
        });
        cut.FindAll(".resource-row-link")[1].Click();
        cut.Render();
        Assert.Equal("/traces/resource/browser?pane=telemetry", cut.Instance.GetTabUrl(ResourcesLayout.ResourceTab.Traces));
        Assert.Equal("/metrics/resource/browser?duration=15&pane=telemetry",
            cut.Instance.AddSelectionToUrl("/metrics/resource/browser?duration=15"));
        Assert.Null(cut.Instance.GetSelectionTelemetryKeys(null));

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
    public void AppHost_EmptyTelemetryPaneShowsNoSourcesAndNoTelemetryKeys()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")], resourceChannelProvider: () => channel));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.TelemetrySourcesUrl());
        var cut = Render<ResourcesLayout>(builder => builder.AddCascadingValue(viewport));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("No telemetry sources", cut.Find(".resource-pane-message").TextContent.Trim());
            Assert.Empty(cut.Instance.GetAllResourceNames());
            Assert.Empty(cut.Instance.GetSelectionTelemetryKeys(null)!);
            Assert.Equal("All sources", cut.Find(".resource-row-all .resource-row-name").TextContent.Trim());
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
            Assert.Equal(["browser"], cut.Instance.GetAllResourceNames());
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
            Assert.Equal(["tab-Overview", "tab-Console", "tab-StructuredLogs", "tab-Traces", "tab-Metrics"],
                cut.FindComponents<FluentTab>().Where(t => t.Instance.Visible).Select(t => t.Instance.Id));
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
