// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using ResourceStrings = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ApplicationSummaryTests : DashboardTestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_DefaultSetting_ShowsSummaryAndOnlyAvailableLayouts(bool graphEnabled)
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = !graphEnabled);
        var cut = RenderSummary();
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));

        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Single(cut.FindAll(".home-insights"));
        Assert.Single(cut.FindAll(".home-problems"));
        Assert.Empty(cut.FindAll(".resource-card, .home-resource-table"));
        Assert.Equal(graphEnabled ? ["Summary", "Graph"] : ["Summary"],
            outlet.FindAll(".icon-toggle").Select(e => e.GetAttribute("aria-label")));
        Assert.IsType<Icons.Regular.Size20.DataBarVertical>(
            outlet.FindComponent<IconToggleGroup<GroupOverview>>().Instance.Options.Single(option => option.Value == GroupOverview.Summary).Icon);
    }

    [Fact]
    public async Task Render_SelectionChanges_UpdatesSummaryWithoutChangingLayout()
    {
        var cut = RenderSummary();
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["api"])));
        cut.WaitForAssertion(() => Assert.Equal("1 resource is running", cut.Find(".home-hero-headline").TextContent));
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Single(cut.FindAll(".home-insights"));
        Assert.Single(cut.FindAll(".home-problems"));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["broken"])));
        cut.WaitForAssertion(() => Assert.Equal("1 resource needs attention", cut.Find(".home-hero-headline").TextContent));
        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["api", "broken"])));
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(GroupOverview.Summary, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
    }

    [Theory]
    [InlineData(KnownResourceState.Running, 1, "1 resource is running")]
    [InlineData(KnownResourceState.Running, 2, "All 2 resources are running")]
    [InlineData(KnownResourceState.Starting, 1, "1 resource is pending")]
    [InlineData(KnownResourceState.Starting, 2, "2 of 2 resources are pending")]
    [InlineData(KnownResourceState.FailedToStart, 1, "1 resource needs attention")]
    [InlineData(KnownResourceState.FailedToStart, 2, "2 of 2 resources need attention")]
    [InlineData(KnownResourceState.Running, 0, "No resources found")]
    public void Render_ResourceCount_UsesSingularAndPluralHeadlines(KnownResourceState state, int count, string expectedHeadline)
    {
        var resources = Enumerable.Range(0, count)
            .Select(i => ModelTestHelpers.CreateResource($"resource-{i}", state: state))
            .ToList();
        var cut = RenderSummary(resources: resources);

        cut.WaitForAssertion(() => Assert.Equal(expectedHeadline, cut.Find(".home-hero-headline").TextContent));
    }

    [Fact]
    public async Task Render_ActivityAndInsights_UseLocalizedText()
    {
        var loc = new TestStringLocalizer<ResourceStrings>();
        Services.AddSingleton<IStringLocalizer<ResourceStrings>>(loc);
        var cut = RenderSummary();

        Assert.Equal(loc[nameof(ResourceStrings.HomeLiveActivity)], cut.Find("#home-activity-title").TextContent);
        Assert.Equal(loc[nameof(ResourceStrings.HomeActivityWindow)], cut.Find(".home-activity-window").TextContent);
        cut.WaitForAssertion(() => Assert.Equal(loc[nameof(ResourceStrings.HomeActivityWaiting)], cut.Find(".home-chart-empty").TextContent));
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeLogsLabel), nameof(ResourceStrings.HomeTracesLabel), nameof(ResourceStrings.HomeMetricsLabel) }
                .Select(key => $"0 {loc[key]}"),
            cut.FindAll(".home-activity-totals a").Select(element => element.TextContent));
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeRequestsPerMinute), nameof(ResourceStrings.HomeErrorRate), nameof(ResourceStrings.HomeP95Latency) }
                .Select(key => loc[key].Value),
            cut.FindAll(".home-stat-label").Select(element => element.TextContent));
        Assert.Equal(loc[nameof(ResourceStrings.HomeActivityChartLabel)], cut.Find(".home-chart-svg").GetAttribute("aria-label"));
        Assert.Equal(loc[nameof(ResourceStrings.HomeActivityWaiting)], cut.Find(".home-chart-empty").TextContent);
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeActivityWindowStart), nameof(ResourceStrings.HomeActivityWindowEnd) }.Select(key => loc[key].Value),
            cut.FindAll(".home-chart-axis span").Select(element => element.TextContent));
        Assert.Equal(loc[nameof(ResourceStrings.HomeInsightsLabel)], cut.Find(".home-insights").GetAttribute("aria-label"));
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeSlowestEndpoints), nameof(ResourceStrings.HomeBusiestResources), nameof(ResourceStrings.HomeErrorHotspots) }
                .Select(key => loc[key].Value),
            cut.FindAll(".home-insight h2").Select(element => element.TextContent));
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeP95Label), nameof(ResourceStrings.HomeRequestsPerMinute), nameof(ResourceStrings.HomeErrorHotspotsHint) }
                .Select(key => loc[key].Value),
            cut.FindAll(".home-insight-hint").Select(element => element.TextContent));
        Assert.Equal(
            new[] { nameof(ResourceStrings.HomeNoRecentRequests), nameof(ResourceStrings.HomeNoRecentRequests), nameof(ResourceStrings.HomeNoRecentActivityErrors) }
                .Select(key => loc[key].Value),
            cut.FindAll(".home-insight .home-empty").Select(element => element.TextContent.Trim()));

        var now = Services.GetRequiredService<BrowserTimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-1);
        await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource("api"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("trace", "span", now, now.AddMilliseconds(10), kind: Span.Types.SpanKind.Server,
                            status: new Status { Code = Status.Types.StatusCode.Error }) }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            var insights = cut.FindAll(".home-insight");
            Assert.Equal(loc[nameof(ResourceStrings.HomeRequestCountShort), "1"], insights[0].QuerySelector(".home-event-meta > span:last-child")!.TextContent);
            Assert.Equal(loc[nameof(ResourceStrings.HomeEndpointTooltip), "api", "Test span. Id: span", "1"],
                insights[0].QuerySelector("a")!.GetAttribute("data-tooltip"));
            Assert.Equal(
                new[] { loc[nameof(ResourceStrings.HomeRequestCount), "1"].Value, loc[nameof(ResourceStrings.HomeFailedCount), "1"].Value },
                insights[1].QuerySelectorAll(".home-event-meta > span:not([aria-hidden])").Select(element => element.TextContent));
            Assert.Equal(
                new[] { loc[nameof(ResourceStrings.HomeFailedRequestCount), "1"].Value, loc[nameof(ResourceStrings.HomeErrorLogCount), "0"].Value },
                insights[2].QuerySelectorAll(".home-event-meta > span:not([aria-hidden])").Select(element => element.TextContent));
            var bucketStart = new DateTime(now.Ticks / ApplicationSummary.s_activityBucketSize.Ticks * ApplicationSummary.s_activityBucketSize.Ticks, DateTimeKind.Utc);
            Assert.Equal(loc[nameof(ResourceStrings.HomeActivityBucketTooltip),
                FormatHelpers.FormatTimeWithOptionalDate(Services.GetRequiredService<BrowserTimeProvider>(), bucketStart), "1", "1"],
                cut.Find(".home-chart-bar").GetAttribute("data-tooltip"));
        });
    }

    [Fact]
    public async Task Render_GraphSetting_ShowsTheResourceGraph()
    {
        var module = JSInterop.SetupModule("/js/app-resourcegraph.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderSummary();

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Empty(cut.FindAll(".home-hero"));
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "initializeResourcesGraph");
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "updateResourcesGraph");
    }

    [Fact]
    public async Task Render_GraphSettingWhenGraphDisabled_ShowsSummary()
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        var cut = RenderSummary();
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
        Assert.Equal(["Summary"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Render_LayoutToggleChangesViewsWithMissingParameters(bool isDesktop, bool missingParameters)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: !isDesktop);
        JSInterop.SetupModule("/js/app-resourcegraph.js").Mode = JSRuntimeMode.Loose;
        var cut = RenderSummary(viewport, missingParameters);
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        outlet.WaitForAssertion(() =>
        {
            Assert.Equal("Layout", outlet.Find(".icon-toggle-group").GetAttribute("aria-label"));
            Assert.Equal(["Summary", "Graph"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
            Assert.Equal(missingParameters ? 1 : 0, outlet.FindAll(".missing-parameters-link").Count);
            Assert.Equal("true", outlet.Find("[aria-label='Summary']").GetAttribute("aria-pressed"));
        });
        if (missingParameters)
        {
            Assert.Contains("parameter", outlet.Find(".missing-parameters-link").GetAttribute("href"));
        }

        outlet.Find("[aria-label='Graph']").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Graph']").GetAttribute("aria-pressed"));

        outlet.Find("[aria-label='Summary']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Summary']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Recording_ActivityWindowEndsAtLastRequest()
    {
        // Much older than the activity window, which would be empty if it ended at the current time.
        var requestTime = new DateTime(2020, 1, 15, 12, 0, 10, DateTimeKind.Utc);
        var cut = await RenderRecordingSummaryAsync(writer => writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1", startTime: requestTime, endTime: requestTime.AddSeconds(1), kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Activity", cut.Find("#home-activity-title").TextContent);
            Assert.Empty(cut.FindAll(".home-live-dot"));
            Assert.Single(cut.FindAll(".home-chart-bar"));
            Assert.Equal(2, cut.FindAll(".home-chart-axis span").Count);
        });
    }

    [Fact]
    public async Task Recording_ManyErrorLogsAfterWindow_DoesNotHideErrorsInWindow()
    {
        var requestTime = new DateTime(2020, 1, 15, 12, 0, 10, DateTimeKind.Utc);
        var cut = await RenderRecordingSummaryAsync(async writer =>
        {
            await writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = TelemetryTestHelpers.CreateResource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = TelemetryTestHelpers.CreateScope(),
                            Spans =
                            {
                                TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1", startTime: requestTime, endTime: requestTime.AddSeconds(1), kind: Span.Types.SpanKind.Server)
                            }
                        }
                    }
                }
            });

            // More logs after the window than the activity summary reads, and one in the window.
            var logTimes = Enumerable.Range(1, 501).Select(i => requestTime.AddHours(1).AddSeconds(i)).Prepend(requestTime);
            await writer.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = TelemetryTestHelpers.CreateResource(),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = TelemetryTestHelpers.CreateScope(),
                            LogRecords = { logTimes.Select(time => TelemetryTestHelpers.CreateLogRecord(time: time, observedTime: time, severity: SeverityNumber.Error)) }
                        }
                    }
                }
            });
        });

        cut.WaitForAssertion(() =>
        {
            var hotspot = cut.FindAll(".home-event-meta span").Select(span => span.TextContent);
            Assert.Contains("1 error logs", hotspot);
        }, DefaultWaitTimeout);
    }

    [Fact]
    public async Task Recording_WithoutRequests_HidesActivityAxis()
    {
        var cut = await RenderRecordingSummaryAsync(_ => Task.CompletedTask);

        cut.WaitForAssertion(() => Assert.Equal("No requests were recorded in this run.", cut.Find(".home-chart-empty").TextContent));
        Assert.Empty(cut.FindAll(".home-chart-axis"));
    }

    [Fact]
    public async Task Live_RequestsAgeOutOfActivityWindowWithoutNewTelemetry()
    {
        var context = await RenderLiveSummaryAsync(GroupOverview.Summary);
        var cut = context.Summary;
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".home-chart-bar"));
            Assert.All(cut.FindAll(".home-stat-value").Skip(1), stat => Assert.NotEqual("–", stat.TextContent));
        }, DefaultWaitTimeout);
        var requestRate = cut.Find(".home-stat-value").TextContent;

        context.Clock.UtcNow += TimeSpan.FromMinutes(16);
        context.Timers.Advance(TimeSpan.FromSeconds(30));

        // The request rate, error rate and latency expire with the request, not only the chart.
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".home-chart-bar"));
            Assert.Equal(["–", "–"], cut.FindAll(".home-stat-value").Skip(1).Select(stat => stat.TextContent));
            Assert.NotEqual(requestRate, cut.Find(".home-stat-value").TextContent);
        }, DefaultWaitTimeout);
    }

    [Fact]
    public async Task Live_OtherLayoutDoesNotRefreshActivityPeriodically()
    {
        JSInterop.SetupModule("/js/app-resourcegraph.js").Mode = JSRuntimeMode.Loose;
        var context = await RenderLiveSummaryAsync(GroupOverview.Graph);
        context.Summary.WaitForAssertion(() => Assert.True(Volatile.Read(ref context.RequestQueries.Count) > 0), DefaultWaitTimeout);
        var queries = Volatile.Read(ref context.RequestQueries.Count);

        context.Timers.Advance(TimeSpan.FromSeconds(30));

        // Longer than the delay before a queued refresh runs.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(queries, Volatile.Read(ref context.RequestQueries.Count));
    }

    [Fact]
    public async Task Live_DisposalStopsPeriodicRefresh()
    {
        var context = await RenderLiveSummaryAsync(GroupOverview.Summary);
        context.Summary.WaitForAssertion(() => Assert.Single(context.Summary.FindAll(".home-chart-bar")), DefaultWaitTimeout);

        await DisposeComponentsAsync();
        var queries = Volatile.Read(ref context.RequestQueries.Count);
        context.Timers.Advance(TimeSpan.FromSeconds(30));

        // Longer than the delay before a queued refresh runs.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(queries, Volatile.Read(ref context.RequestQueries.Count));
    }

    private async Task<LiveSummaryContext> RenderLiveSummaryAsync(GroupOverview overview)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var timers = new FakeTimeProvider();
        var clock = new TestTimeProvider { Timers = timers };
        var requestQueries = new QueryCounter();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("TestService-TestId", state: KnownResourceState.Running, resourceType: "Project")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services =>
            {
                services.AddSingleton<BrowserTimeProvider>(clock);
                services.AddSingleton<ITelemetryRepository>(provider =>
                {
                    var inner = provider.GetRequiredService<SqliteTelemetryRepository>();
                    return new TestTelemetryRepository(inner)
                    {
                        GetRequestSpansAsyncHandler = (request, token) =>
                        {
                            Interlocked.Increment(ref requestQueries.Count);
                            return inner.GetRequestSpansAsync(request, token);
                        }
                    };
                });
            });

        var requestTime = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1", startTime: requestTime, endTime: requestTime.AddMilliseconds(5), kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        });

        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(overview));
        return new LiveSummaryContext(cut, clock, timers, requestQueries);
    }

    private async Task<IRenderedComponent<ApplicationSummary>> RenderRecordingSummaryAsync(Func<ITelemetryRepositoryWriter, Task> seed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Finished, resourceType: "Project")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: true),
            configureServices: services => FluentUISetupHelpers.AddTelemetryRepositoryConfiguration(services, readOnly: true));
        await FluentUISetupHelpers.SeedTelemetryRepositoryAsync(this, seed);

        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Summary));
        return cut;
    }

    private IRenderedComponent<ApplicationSummary> RenderSummary(ViewportInformation? viewport = null, bool missingParameters = false,
        IList<ResourceViewModel>? resources = null)
    {
        viewport ??= new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: resources ??
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("broken", state: KnownResourceState.FailedToStart, resourceType: "Container"),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter,
                    state: missingParameters ? KnownResourceState.ValueMissing : KnownResourceState.Running)
            ],
            resourceChannelProvider: () => channel));

        return Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
    }

    private sealed class QueryCounter
    {
        public int Count;
    }

    private sealed record LiveSummaryContext(IRenderedComponent<ApplicationSummary> Summary, TestTimeProvider Clock, FakeTimeProvider Timers, QueryCounter RequestQueries);
}
