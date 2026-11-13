// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourceRowAvailabilityTests : DashboardTestContext
{
    [Theory]
    [InlineData("/resources", null)]
    [InlineData("/consolelogs", "Parameters do not produce console logs. Select to view this parameter's overview.")]
    [InlineData("/structuredlogs", "Parameters do not produce structured logs. Select to view this parameter's overview.")]
    [InlineData("/traces", "Parameters do not produce traces. Select to view this parameter's overview.")]
    [InlineData("/metrics", "Parameters do not produce metrics. Select to view this parameter's overview.")]
    [InlineData("/terminals", "Parameters do not support terminal sessions. Select to view this parameter's overview.")]
    public async Task ParameterRows_ReflectCurrentTabAndRemainNavigable(string url, string? reason)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);

        var row = cut.Find("[data-resource-name='key']");
        var link = row.QuerySelector(".resource-row-link")!;
        var unavailable = reason is not null;
        Assert.Equal(unavailable, row.ClassList.Contains("resource-row-unavailable"));
        Assert.Equal(reason, link.GetAttribute("data-resource-unavailable-reason"));
        Assert.Equal(reason, link.GetAttribute("data-tooltip"));
        if (reason is not null)
        {
            Assert.Contains(reason, link.GetAttribute("aria-description"));
        }
        Assert.False(link.HasAttribute("disabled"));
        Assert.False(link.HasAttribute("aria-disabled"));
        Assert.False(link.HasAttribute("tabindex"));
        Assert.Equal("/resources/key", link.GetAttribute("href"));

        await link.ClickAsync();
        cut.Render();

        Assert.Equal("resources/key", navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(["key"], cut.Instance.SelectedResourceNames);
        Assert.False(cut.Find("[data-resource-name='key']").ClassList.Contains("resource-row-unavailable"));
        Assert.Null(cut.Find("[data-resource-name='key'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
    }

    [Fact]
    public async Task CompactDensity_UnavailableRowKeepsReasonWithoutBadge()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);
        // Rows have no availability badge at any density: an unavailable row is muted instead.
        Assert.Empty(cut.FindAll("[data-resource-name='test-TestId'] .resource-row-availability"));

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Compact));

        // A single-line row stays muted and keeps the reason in its tooltip and accessible description.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-pane-list.density-compact")));
        var row = cut.Find("[data-resource-name='test-TestId']");
        var link = row.QuerySelector(".resource-row-link")!;
        Assert.Empty(row.QuerySelectorAll(".resource-row-availability"));
        Assert.True(row.ClassList.Contains("resource-row-unavailable"));
        Assert.Equal(Resources.Layout.ResourcePaneNoStructuredLogs, link.GetAttribute("data-tooltip"));
        Assert.Contains(Resources.Layout.ResourcePaneNoStructuredLogs, link.GetAttribute("aria-description"));
    }

    [Fact]
    public async Task ClosedContent_ShowsEveryRowAvailableUntilAViewOpens()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test-TestId"), ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);
        Assert.Equal(["test-TestId", "key"], GetUnavailableRows());

        // Showing the full resource list leaves no view selected, so nothing is unavailable for it any more. The data
        // tabs have no close button for it (the full list lives on the Overview tab), so the test opens it directly.
        ResourceSetupHelpers.ToggleResourceList(cut);
        cut.Render();
        Assert.True(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Empty(GetUnavailableRows());
        Assert.Empty(cut.FindAll(".resource-row-availability"));
        Assert.Null(cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-tooltip"));

        // Opening the view again shows which rows have nothing for it.
        await cut.InvokeAsync(() => cut.Instance.OpenTabFromFullCanvasAsync("tab-StructuredLogs"));
        cut.Render();
        Assert.False(cut.Find(".resources-layout").ClassList.Contains("pane-full-canvas"));
        Assert.Equal(["test-TestId", "key"], GetUnavailableRows());

        IEnumerable<string?> GetUnavailableRows() =>
            cut.FindAll(".resource-row.resource-row-unavailable").Select(row => row.GetAttribute("data-resource-name"));
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs, "/structuredlogs", "/structuredlogs/resource/test-TestId")]
    [InlineData(AspireDataType.Traces, "/traces", "/traces/resource/test-TestId")]
    [InlineData(AspireDataType.Metrics, "/metrics", "/metrics/resource/test-TestId")]
    public async Task SignalAvailability_UpdatesWithoutChangingSelectionOrNavigation(AspireDataType signal, string url, string selectedUrl)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, client);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);
        var tooltip = signal switch
        {
            AspireDataType.StructuredLogs => Resources.Layout.ResourcePaneNoStructuredLogs,
            AspireDataType.Traces => Resources.Layout.ResourcePaneNoTraces,
            _ => Resources.Layout.ResourcePaneNoMetrics
        };
        var link = cut.Find("[data-resource-name='test-TestId'] .resource-row-link");
        Assert.Equal(tooltip, link.GetAttribute("data-resource-unavailable-reason"));
        Assert.Equal(tooltip, link.GetAttribute("data-tooltip"));
        Assert.Contains(tooltip, link.GetAttribute("aria-description"));
        Assert.False(link.HasAttribute("aria-disabled"));

        await link.ClickAsync();
        cut.Render();
        Assert.Equal(selectedUrl.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames);
        Assert.True(cut.Find("[data-resource-name='test-TestId']").ClassList.Contains("selected"));
        Assert.True(cut.Find("[data-resource-name='test-TestId']").ClassList.Contains("resource-row-unavailable"));
        Assert.Empty(cut.Instance.GetSelectionTelemetryKeys(null)!);

        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, signal);
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("[data-resource-name='test-TestId']").ClassList.Contains("resource-row-unavailable"));
            Assert.Null(cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-tooltip"));
            Assert.Null(cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
        });
        Assert.Equal(selectedUrl.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames);
        Assert.Equal(["test-TestId"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.GetCompositeName()));

        await repository.ClearSelectedSignalsAsync(new() { ["test-TestId"] = [signal] });
        cut.WaitForAssertion(() => Assert.Equal(tooltip, cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-tooltip")));
        Assert.Equal(tooltip, cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
        Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames);
        Assert.Equal(selectedUrl.TrimStart('/'), navigation.ToBaseRelativePath(navigation.Uri));
        Assert.Equal(1, client.ResourceSubscriptionCount);
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs, "/structuredlogs")]
    [InlineData(AspireDataType.Traces, "/traces")]
    public async Task RetentionEvictsLastItem_RestoresUnavailableStateWithoutChangingSelection(AspireDataType signal, string url)
    {
        var reason = signal == AspireDataType.StructuredLogs ? Resources.Layout.ResourcePaneNoStructuredLogs : Resources.Layout.ResourcePaneNoTraces;
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions
            {
                TelemetryLimits = { MaxLogCount = 1, MaxTraceCount = 1 }
            })));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, signal);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);
        Assert.Empty(cut.FindAll("[data-resource-name='test-TestId'] .resource-row-availability"));
        await cut.Find("[data-resource-name='test-TestId'] .resource-row-link").ClickAsync();
        cut.Render();
        cut.WaitForAssertion(() => Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames));
        var selectedUri = navigation.Uri;

        if (signal == AspireDataType.StructuredLogs)
        {
            await repository.AddLogsAsync(new(),
            [
                new ResourceLogs
                {
                    Resource = CreateResource("other"),
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(time: DateTime.UtcNow.AddDays(1)) } } }
                }
            ]);
        }
        else
        {
            await repository.AddTracesAsync(new(),
            [
                new ResourceSpans
                {
                    Resource = CreateResource("other"),
                    ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans = { CreateSpan("other-trace", "other-span", DateTime.UnixEpoch.AddDays(1), DateTime.UnixEpoch.AddDays(1).AddSeconds(1)) } } }
                }
            ]);
        }

        cut.WaitForAssertion(() => Assert.Equal(reason,
            cut.Find("[data-resource-name='test-TestId'] .resource-row-link").GetAttribute("data-resource-unavailable-reason")));
        Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames);
        Assert.Equal(selectedUri, navigation.Uri);
        var resource = repository.GetResourceByCompositeName("test-TestId")!;
        Assert.True(signal == AspireDataType.StructuredLogs ? resource.HasLogs : resource.HasTraces);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(AspireDataType.StructuredLogs, false)]
    [InlineData(AspireDataType.Metrics, false)]
    [InlineData(AspireDataType.StructuredLogs, true)]
    [InlineData(AspireDataType.Metrics, true)]
    public async Task Peer_IsAvailableForTracesUntilItsOutgoingSpanIsCleared(AspireDataType? reportedSignal, bool reportAfterSpan)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("cache",
                urls: [new("tcp", new Uri("tcp://localhost:6379"), false, false, UrlDisplayPropertiesViewModel.Empty)])],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<IOutgoingPeerResolver, ResourceOutgoingPeerResolver>());
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        KeyValuePair<string, string>[] attributes = [KeyValuePair.Create("peer.service", "localhost:6379")];
        var resolver = Services.GetRequiredService<IOutgoingPeerResolver>();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => Task.FromResult(resolver.TryResolvePeer(attributes, out _, out _)),
            "The resource peer resolver did not receive its initial resource snapshot.");
        if (reportedSignal is not null && !reportAfterSpan)
        {
            await AddPeerSignalAsync();
        }
        await repository.AddTracesAsync(new(),
        [
            new ResourceSpans
            {
                Resource = CreateResource("api"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan("trace", "span", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1),
                                attributes: attributes, kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        ]);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/traces/resource/cache");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);

        Assert.Equal(reportedSignal is null || reportAfterSpan, repository.GetResourceByCompositeName("cache")!.UninstrumentedPeer);
        Assert.False(cut.Find("[data-resource-name='cache']").ClassList.Contains("resource-row-unavailable"));
        Assert.Null(cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);
        Assert.Equal(["cache"], cut.Instance.GetSelectionTelemetryKeys(null)!.Select(key => key.Name));

        if (reportedSignal is not null && reportAfterSpan)
        {
            await AddPeerSignalAsync();
            cut.WaitForAssertion(() =>
            {
                Assert.False(repository.GetResourceByCompositeName("cache")!.UninstrumentedPeer);
                Assert.False(cut.Find("[data-resource-name='cache']").ClassList.Contains("resource-row-unavailable"));
                Assert.Null(cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
            });
        }

        var traces = await repository.GetTracesAsync(GetTracesRequest.ForResourceKey(new("cache", null)), CancellationToken.None);
        Assert.Single(traces.PagedResult.Items);
        await repository.ClearSelectedSignalsAsync(new() { ["api-TestId"] = [AspireDataType.Traces] });
        cut.WaitForAssertion(() => Assert.Equal(Resources.Layout.ResourcePaneNoTraces,
            cut.Find("[data-resource-name='cache'] .resource-row-link").GetAttribute("data-resource-unavailable-reason")));
        Assert.Equal(["cache"], cut.Instance.SelectedResourceNames);

        Task AddPeerSignalAsync()
        {
            var peer = CreateResource("cache");
            peer.Attributes.Remove(peer.Attributes.Single(attribute => attribute.Key == "service.instance.id"));
            return reportedSignal switch
            {
                AspireDataType.StructuredLogs => repository.AddLogsAsync(new(),
                [
                    new ResourceLogs
                    {
                        Resource = peer,
                        ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: "Peer log") } } }
                    }
                ]),
                AspireDataType.Metrics => repository.AddMetricsAsync(new(),
                [
                    new ResourceMetrics
                    {
                        Resource = peer,
                        ScopeMetrics = { new ScopeMetrics { Scope = CreateScope(), Metrics = { CreateSumMetric("peer.requests", DateTime.UnixEpoch) } } }
                    }
                ]),
                _ => throw new InvalidOperationException("The test must specify a peer signal.")
            };
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AmbiguousCompositeNames_DoNotBreakLayoutOrAssociateAnArbitrarySource(bool appModelEnabled, bool addCollisionAfterRender)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: appModelEnabled,
            initialResources: [ModelTestHelpers.CreateResource("api-a-1")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await AddSourceAsync("api-a", "1");
        if (!addCollisionAfterRender)
        {
            await AddSourceAsync("api", "a-1");
        }
        Services.GetRequiredService<NavigationManager>().NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);
        if (addCollisionAfterRender)
        {
            await AddSourceAsync("api", "a-1");
        }
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, repository.GetResources().Count);
            Assert.Equal(2, cut.FindAll(".resource-row-kind-telemetry").Count);
            if (appModelEnabled)
            {
                Assert.Equal(Resources.Layout.ResourcePaneNoStructuredLogs,
                    cut.Find(".resource-row-kind-resource[data-resource-name='api-a-1'] .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
                Assert.Empty(cut.Instance.GetSelectionTelemetryKeys(["api-a-1"])!);
            }
        });

        Task AddSourceAsync(string name, string instanceId) => repository.AddLogsAsync(new(),
        [
            new ResourceLogs
            {
                Resource = CreateResource(name, instanceId),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: name) } } }
            }
        ]);
    }

    [Fact]
    public async Task OtherSignals_DoNotMakeStructuredLogsAvailableForTelemetryOnlyRows()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, AspireDataType.Metrics);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/structuredlogs");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);

        Assert.Equal(Resources.Layout.ResourcePaneNoStructuredLogs, cut.Find(".resource-row-kind-telemetry .resource-row-link").GetAttribute("data-tooltip"));
        Assert.Equal(Resources.Layout.ResourcePaneNoStructuredLogs, cut.Find(".resource-row-kind-telemetry .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
        await cut.Find(".resource-row-kind-telemetry .resource-row-link").ClickAsync();
        cut.Render();
        Assert.Equal(["test"], cut.Instance.SelectedResourceNames);
        Assert.Equal("structuredlogs/resource/test", navigation.ToBaseRelativePath(navigation.Uri));

        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, AspireDataType.StructuredLogs);
        cut.WaitForAssertion(() => Assert.Null(cut.Find(".resource-row-kind-telemetry .resource-row-link").GetAttribute("data-tooltip")));
        Assert.Null(cut.Find(".resource-row-kind-telemetry .resource-row-link").GetAttribute("data-resource-unavailable-reason"));
        Assert.Equal(["test"], cut.Instance.SelectedResourceNames);
    }

    [Theory]
    [InlineData("/structuredlogs", false)]
    [InlineData("/traces", true)]
    [InlineData("/metrics", true)]
    [InlineData("/consolelogs", false)]
    [InlineData("/terminals", true)]
    public async Task AvailableLogs_DoNotImplyOtherSignalsOrTerminalCapability(string url, bool unavailable)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        await TelemetryHelpSetupHelpers.AddSignalAsync(Services.GetRequiredService<SqliteTelemetryRepository>(), AspireDataType.StructuredLogs);
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await UseComfortableDensityAsync(cut);

        Assert.Equal(unavailable, cut.Find("[data-resource-name='test-TestId']").ClassList.Contains("resource-row-unavailable"));
    }

    // The "No logs" and "Not applicable" badges are on the second line of a row, so these tests use a two-line
    // density. The list starts compact, where an unavailable row is only muted (see
    // CompactDensity_UnavailableRowKeepsReasonWithoutBadge). Set after loading, which applies stored preferences.
    private Task UseComfortableDensityAsync(IRenderedComponent<ResourcesLayout> cut) =>
        cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetDensityAsync(ResourceListDensity.Comfortable));
}
