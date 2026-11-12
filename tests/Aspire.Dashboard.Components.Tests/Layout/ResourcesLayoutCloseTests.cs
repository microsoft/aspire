// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutCloseTests : DashboardTestContext
{
    // The full resource table lives on the Overview tab, so the data tabs have no page-level close button. Their own
    // back links (such as "Traces" on a trace) and the tabs cover navigation there.
    [Theory]
    [InlineData("/consolelogs")]
    [InlineData("/consolelogs?filter=example")]
    [InlineData("/structuredlogs?logLevel=Error")]
    [InlineData("/traces?type=server")]
    [InlineData("/metrics?duration=30")]
    [InlineData("/consolelogs/resource/api")]
    [InlineData("/structuredlogs/resource/api?logLevel=Error")]
    [InlineData("/traces/resource/api?type=server")]
    [InlineData("/metrics/resource/api?duration=30")]
    [InlineData("/metrics/resource/api/meter/Test")]
    [InlineData("/metrics/resource/api/meter/Test/instrument/Requests?duration=30")]
    [InlineData("/Metrics/Resource/api/Meter/Test/Instrument/Requests?duration=30")]
    [InlineData("/Metrics/Resource/api/Meter/Test")]
    [InlineData("/metrics/resource/api?meter=Test&instrument=Requests&duration=30")]
    [InlineData("/metrics/resource/api?meter=Test&duration=30")]
    [InlineData("/traces/detail/missing")]
    [InlineData("/Traces/Detail/missing")]
    [InlineData("/traces/detail/missing?resource=api&type=server&filters=example&spanId=span")]
    [InlineData("/traces/detail/missing?resource=api&resource=cache&type=server")]
    [InlineData("/traces/detail/missing?pane=telemetry")]
    public void DataTabs_HaveNoPageCloseButton(string url)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
    }

    [Theory]
    [InlineData("/resources", "/resources", true)]
    [InlineData("/terminals", "/terminals", true)]
    [InlineData("/resources?resource=api&resource=cache", "/resources", true)]
    [InlineData("/resources/api", "/resources", true)]
    [InlineData("/resources/api?pane=tags", "/resources", true)]
    [InlineData("/resources/missing", "/resources", true)]
    [InlineData("/resources/key", "/resources", true)]
    [InlineData("/resources/api?terminal=true", "/resources", true)]
    [InlineData("/terminals/resource/api", "/resources", true)]
    public async Task ResourcePages_HaveNoPageCloseButton(string url, string expectedParent, bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache"),
                ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await OpenStartPageContentAsync(cut, url, isDesktop);

        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
        Assert.Equal(expectedParent, cut.Instance.DetailParentUrl);
        Assert.Equal(url.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Theory]
    [InlineData("/resources", true)]
    public async Task TableSelector_ExpandsOverviewAndSummaryReopensContent(string url, bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<Components.Controls.ApplicationSummary>(0);
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await OpenStartPageContentAsync(cut, url, isDesktop);

        await cut.Find(".resource-header-actions [aria-label='Table']").ClickAsync();
        cut.Render();
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));

        await cut.Find(".resource-row-all .resource-row-link").ClickAsync();
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        await cut.Find(".resource-pane-view-selector [aria-label='Summary']").ClickAsync();
        cut.Render();

        Assert.Equal(url.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
    }

    [Theory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces?resource=api")]
    [InlineData("/traces/detail/missing")]
    public async Task Table_UnrelatedNavigationOpensContent(string nextUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.InvokeAsync(() => cut.Instance.SetResourceListVisible(true));
        cut.Render();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        navigation.NavigateTo(nextUrl);
        cut.Render();

        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
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
    public async Task ResourceDropdown_OpensAndClosesDrawerWithoutPageCloseButton(string url, string expectedDestination)
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
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
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
    }

    [Fact]
    public async Task ViewportChange_UsesResourceDropdownWithoutPageCloseButton()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = desktop with { IsDesktop = false };
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");
        var cut = Render<CascadingValue<ViewportInformation>>(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ResourcesLayout>().Instance.IsLoaded));
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());

        cut.Render(p => p.Add(v => v.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
        await cut.Find(".resource-picker").ClickAsync();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        cut.Render(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
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

    // The dashboard opens /resources on the full-page list (see ResourcesLayout.TryRedirectLandingAsync), so there's
    // no content to close until Summary opens the overview.
    private static async Task OpenStartPageContentAsync(IRenderedComponent<ResourcesLayout> cut, string url, bool isDesktop)
    {
        if (isDesktop && url == DashboardUrls.ResourceListUrl())
        {
            Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
            await cut.Find(".resource-pane-view-selector [aria-label='Summary']").ClickAsync();
            cut.Render();
            Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        }
    }
}
