// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutCloseTests : DashboardTestContext
{
    [Theory]
    [InlineData("/resources", "/resources", true)]
    [InlineData("/consolelogs", "/consolelogs", true)]
    [InlineData("/consolelogs?filter=example", "/consolelogs?filter=example", true)]
    [InlineData("/terminals", "/terminals", true)]
    [InlineData("/structuredlogs?logLevel=Error", "/structuredlogs?logLevel=Error", true)]
    [InlineData("/traces?type=server", "/traces?type=server", true)]
    [InlineData("/metrics?duration=30", "/metrics?duration=30", true)]
    [InlineData("/resources?resource=api&resource=cache", "/resources", true)]
    [InlineData("/resources/api", "/resources", true)]
    [InlineData("/resources/api?pane=tags", "/resources", true)]
    [InlineData("/resources/missing", "/resources", true)]
    [InlineData("/resources/key", "/resources", true)]
    [InlineData("/resources/api?terminal=true", "/resources", true)]
    [InlineData("/consolelogs/resource/api", "/consolelogs", true)]
    [InlineData("/terminals/resource/api", "/resources", true)]
    [InlineData("/structuredlogs/resource/api?logLevel=Error", "/structuredlogs?logLevel=Error", true)]
    [InlineData("/traces/resource/api?type=server", "/traces?type=server", true)]
    [InlineData("/metrics/resource/api?duration=30", "/metrics?duration=30", true)]
    [InlineData("/metrics/resource/api/meter/Test", "/metrics/resource/api", true)]
    [InlineData("/metrics/resource/api/meter/Test/instrument/Requests?duration=30", "/metrics/resource/api/meter/Test?duration=30", true)]
    [InlineData("/Metrics/Resource/api/Meter/Test/Instrument/Requests?duration=30", "/Metrics/Resource/api/Meter/Test?duration=30", true)]
    [InlineData("/Metrics/Resource/api/Meter/Test", "/metrics/resource/api", true)]
    [InlineData("/metrics/resource/api?meter=Test&instrument=Requests&duration=30", "/metrics/resource/api?meter=Test&duration=30", true)]
    [InlineData("/metrics/resource/api?meter=Test&duration=30", "/metrics/resource/api?duration=30", true)]
    [InlineData("/traces/detail/missing", "/traces", true)]
    [InlineData("/Traces/Detail/missing", "/traces", true)]
    [InlineData("/traces/detail/missing?resource=api&type=server&filters=example&spanId=span", "/traces/resource/api?resource=api&type=server&filters=example", true)]
    [InlineData("/traces/detail/missing?resource=api&resource=cache&type=server", "/traces?resource=api&resource=cache&type=server", true)]
    [InlineData("/traces/detail/missing?pane=telemetry", "/traces?pane=telemetry", true)]
    public async Task CloseDetail_NavigatesToParentAndShowsResourceList(string url, string expectedParent, bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var commandCount = 0;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache"),
                ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            executeResourceCommand: (_, _, _, _, _) =>
            {
                commandCount++;
                return Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Succeeded });
            }));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        var close = Assert.Single(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
        Assert.Equal(expectedParent, close.Instance.ParentUrl);
        Assert.Equal(ButtonAppearance.Default, close.FindComponent<FluentButton>().Instance.Appearance);
        await cut.Find(".detail-page-close").ClickAsync();
        cut.Render();

        Assert.Equal(expectedParent.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(isDesktop, cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Equal(!isDesktop, cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Empty(cut.FindAll(".resource-pane-canvas-toggle"));
        Assert.Equal(0, commandCount);
        if (ResourcesLayout.ParseLocation(expectedParent).SelectedResourceNames.Count == 0)
        {
            Assert.Empty(cut.Instance.SelectedResourceNames);
        }

        if (!url.Contains("pane=telemetry", StringComparison.Ordinal))
        {
            cut.Find(".resource-row[data-resource-name='cache'] .resource-row-link").Click();
            cut.Render();
            await cut.InvokeAsync(() => cut.Instance.CompletePaneSelectionReveal(navigation.Uri));
            Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
            Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        }
    }

    [Theory]
    [InlineData("/resources", true)]
    [InlineData("/consolelogs", true)]
    [InlineData("/consolelogs?filter=example", true)]
    [InlineData("/structuredlogs", true)]
    [InlineData("/structuredlogs?logLevel=Error", true)]
    [InlineData("/traces", true)]
    [InlineData("/traces?type=server", true)]
    [InlineData("/metrics", true)]
    [InlineData("/metrics?duration=30", true)]
    public async Task SelectAll_ReopensTabAfterClosingContent(string url, bool isDesktop)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        await cut.Find(".detail-page-close").ClickAsync();
        cut.Render();
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));

        await cut.Find(".resource-select-all-toggle").ClickAsync();
        cut.Render();

        Assert.Equal(url.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.Single(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
    }

    [Theory]
    [InlineData("/traces/detail/missing?resource=api&type=server")]
    [InlineData("/metrics/resource/api/meter/Test")]
    [InlineData("/metrics/resource/api?meter=Test&instrument=Requests&duration=30")]
    public async Task SelectedResource_ReopensParentFromFullCanvasWithoutClearingSelection(string url)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        await cut.Find(".detail-page-close").ClickAsync();
        cut.Render();
        var parent = navigation.Uri;
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        await cut.Find(".resource-row[data-resource-name='api'] .resource-row-link").ClickAsync();
        cut.Render();

        Assert.Equal(parent, navigation.Uri);
        Assert.Equal(["api"], cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));

        await cut.Find(".resource-row[data-resource-name='api'] .resource-row-link").ClickAsync();
        cut.Render();
        Assert.Empty(cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public async Task CloseDetail_PreservesFullCanvasAcrossDestinationSessionStateRedirect()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services =>
            {
                services.AddTransient<TracesViewModel>();
                services.AddSingleton<ISessionStorage>(new TestSessionStorage
                {
                    OnGetAsync = key => key == BrowserStorageKeys.TracesPageState
                        ? (true, new Traces.TracesPageState { SelectedSpanType = "server", Filters = [] })
                        : (false, null)
                });
            });
        FluentUISetupHelpers.SetupFluentList(this);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/traces/resource/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));

        await cut.Find(".detail-page-close").ClickAsync();
        cut.Render(p => p.Add(layout => layout.Body, body =>
        {
            body.OpenComponent<Traces>(0);
            body.AddComponentParameter(1, nameof(Traces.ResourceName),
                ResourcesLayout.ParseLocation(navigation.ToBaseRelativePath(navigation.Uri)).RouteResourceName);
            body.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Equal("traces?type=server", navigation.ToBaseRelativePath(navigation.Uri)));
        cut.Render();

        Assert.Single(cut.FindComponents<Traces>());
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.True(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Empty(cut.Instance.SelectedResourceNames);

        await cut.Find(".resource-select-all-toggle").ClickAsync();
        cut.Render();
        Assert.Equal("traces?type=server", navigation.ToBaseRelativePath(navigation.Uri));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
    }

    [Theory]
    [InlineData("/metrics?meter=Test", "metrics")]
    [InlineData("/metrics?meter=Test&instrument=Requests", "metrics?meter=Test")]
    public async Task CloseDetail_HistoryNavigationBackToQueryDetailOpensContent(string url, string expectedParent)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find(".detail-page-close").ClickAsync();
        cut.Render();
        Assert.Equal(expectedParent, navigation.ToBaseRelativePath(navigation.Uri));
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));

        // Browser back reaches the previous detail entry without the page state restoration marker.
        navigation.NavigateTo(url);
        cut.Render();

        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
    }

    [Theory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces?resource=api")]
    [InlineData("/traces/detail/missing")]
    public async Task CloseDetail_UnrelatedNavigationOpensContent(string nextUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/traces");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await cut.Find(".detail-page-close").ClickAsync();
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
        await cut.Find(".resource-select-all-toggle").ClickAsync();
        cut.Render();
        Assert.Equal(expectedDestination.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Empty(cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        Assert.False(cut.Find(".resource-content").HasAttribute("inert"));
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
    }

    [Fact]
    public async Task ViewportChange_SwitchesBetweenCloseButtonAndResourceDropdown()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = desktop with { IsDesktop = false };
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/api");
        var cut = Render<CascadingValue<ViewportInformation>>(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ResourcesLayout>().Instance.IsLoaded));
        Assert.Single(cut.FindComponents<Components.Controls.DetailPageCloseButton>());

        cut.Render(p => p.Add(v => v.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.Empty(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
        await cut.Find(".resource-picker").ClickAsync();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("drawer-open"));
        cut.Render(p => p.Add(v => v.Value, desktop).AddChildContent<ResourcesLayout>());
        Assert.Single(cut.FindComponents<Components.Controls.DetailPageCloseButton>());
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
