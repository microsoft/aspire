// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewConsoleTests : DashboardTestContext
{
    [Fact]
    public async Task Selection_DiscardsAlreadyDeliveredBatchFromCanceledResource()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var resources = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var apiLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var cacheLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api"), ModelTestHelpers.CreateResource("cache")],
            resourceChannelProvider: () => resources,
            consoleLogsChannelProvider: name => name == "api" ? apiLogs : cacheLogs);
        client.BeforeConsoleLogBatchAsync = async (name, _) =>
        {
            if (name == "api")
            {
                delivered.TrySetResult();
                // Model a delivered batch completing after its subscription was canceled.
                await release.Task;
            }
        };
        client.OnConsoleSubscriptionDisposed = name =>
        {
            if (name == "api")
            {
                disposed.TrySetResult();
            }
        };
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "api");
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        await apiLogs.Writer.WriteAsync([new ResourceLogLine(99, "old api output", false)]);
        await delivered.Task.WaitAsync(DefaultWaitTimeout);

        try
        {
            navigation.NavigateTo("/resources/cache");
            cut.Render();
            await cacheLogs.Writer.WriteAsync([new ResourceLogLine(1, "current cache output", false)]);
            cut.WaitForAssertion(() => Assert.Equal(["current cache output"],
                cut.FindAll(".overview-console-line").Select(line => line.TextContent.Trim())));
            release.SetResult();
            await disposed.Task.WaitAsync(DefaultWaitTimeout);
            cut.FindComponent<ResourceOverview>().Render();

            Assert.Equal(["current cache output"],
                cut.FindAll(".overview-console-line").Select(line => line.TextContent.Trim()));
            Assert.Equal("cache", cut.Find(".resource-overview").GetAttribute("data-resource-subscription"));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Selection_FirstAndReplacementResourceLoadWithoutWaitingForLiveRefresh()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ComponentFactories.Add(() =>
        {
            var overview = Activator.CreateInstance<ResourceOverview>();
            overview.TelemetryRefreshDelayAsync = (_, _) =>
            {
                holding.TrySetResult();
                return held.Task;
            };
            return overview;
        });

        try
        {
            var (cut, navigation) = await RenderOverviewWithTelemetryAsync();
            cut.WaitForAssertion(() => AssertTelemetryLoaded(cut), DefaultWaitTimeout);
            await AddSpanAsync("api", DateTime.UtcNow, "held");
            await holding.Task.WaitAsync(DefaultWaitTimeout);

            // The old resource's refresh remains blocked in its delay, even after cancellation.
            navigation.NavigateTo("/resources/cache-TestId");
            cut.Render();

            cut.WaitForAssertion(() =>
            {
                Assert.Equal("cache-TestId", cut.Find(".resource-overview").GetAttribute("data-resource-subscription"));
                AssertTelemetryLoaded(cut);
            }, DefaultWaitTimeout);
        }
        finally
        {
            held.TrySetCanceled();
        }
    }

    [Fact]
    public async Task Selection_OldTelemetryReadCompletesAfterReplacement_KeepsCurrentCountsAndPreview()
    {
        var apiReadStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApiRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timestamp = DateTime.UtcNow;

        try
        {
            var (cut, navigation) = await RenderOverviewWithTelemetryAsync(services =>
                services.AddSingleton<ITelemetryRepository>(provider =>
                {
                    var inner = provider.GetRequiredService<SqliteTelemetryRepository>();
                    return new TestTelemetryRepository(inner)
                    {
                        // Let the whole read succeed after cancellation so the publication guard is what rejects it.
                        GetTraceSummariesAsyncHandler = (request, _) => inner.GetTraceSummariesAsync(request, CancellationToken.None),
                        GetOverviewLogsAsyncHandler = async (keys, _, _, cancellationToken) =>
                        {
                            var key = Assert.Single(keys);
                            var resource = Assert.IsType<OtlpResource>(inner.GetResource(key));
                            if (key.Name == "api")
                            {
                                apiReadStarted.TrySetResult(cancellationToken);
                                await releaseApiRead.Task;
                                return new OverviewLogs(2, 1, 1,
                                [
                                    new OverviewLog(1, timestamp, LogLevel.Error, "old api error", resource),
                                    new OverviewLog(2, timestamp, LogLevel.Warning, "old api warning", resource)
                                ]);
                            }

                            Assert.Equal("cache", key.Name);
                            return new OverviewLogs(1, 0, 0, [new OverviewLog(3, timestamp, LogLevel.Information, "current cache log", resource)]);
                        }
                    };
                }));

            var apiToken = await apiReadStarted.Task.WaitAsync(DefaultWaitTimeout);
            var overview = cut.FindComponent<ResourceOverview>().Instance;
            var apiRefresh = Task.CompletedTask;
            await cut.InvokeAsync(() =>
            {
                apiRefresh = overview.TelemetryRefreshTask;
            });
            Assert.False(apiRefresh.IsCompleted);

            navigation.NavigateTo("/resources/cache-TestId");
            cut.Render();
            cut.WaitForAssertion(() => AssertCacheTelemetry(cut));
            Assert.Same(overview, cut.FindComponent<ResourceOverview>().Instance);
            Assert.True(apiToken.IsCancellationRequested);

            releaseApiRead.SetResult();
            await apiRefresh.WaitAsync(DefaultWaitTimeout);
            await cut.InvokeAsync(() => AssertCacheTelemetry(cut));
        }
        finally
        {
            releaseApiRead.TrySetResult();
        }

        static void AssertCacheTelemetry(IRenderedComponent<ResourcesLayout> cut)
        {
            Assert.Equal("cache-TestId", cut.Find(".resource-overview").GetAttribute("data-resource-subscription"));
            var logs = cut.Find("[aria-labelledby='overview-logs-title']");
            Assert.Equal(["1", "0", "0"], logs.QuerySelectorAll(".overview-stat-value").Select(stat => stat.TextContent));
            Assert.Equal("current cache log", Assert.Single(logs.QuerySelectorAll(".overview-list-value")).TextContent);
        }
    }

    [Fact]
    public async Task ConsoleFilterChange_DoesNotLoseWaitingTelemetryRefresh()
    {
        var (cut, _) = await RenderOverviewWithTelemetryAsync();

        // Replacing the console subscription cancels the refresh that's waiting. No more telemetry arrives to queue another.
        await cut.InvokeAsync(() => Services.GetRequiredService<ConsoleLogsManager>().UpdateFiltersAsync(ConsoleLogsFilters.Default));

        cut.WaitForAssertion(() => AssertTelemetryLoaded(cut), DefaultWaitTimeout);
    }

    private static void AssertTelemetryLoaded(IRenderedComponent<ResourcesLayout> cut)
    {
        Assert.Empty(cut.FindAll(".overview-stats[aria-hidden='true']"));
        Assert.NotEmpty(cut.FindAll("[aria-labelledby='overview-traces-title'] .overview-stats"));
    }

    private async Task<(IRenderedComponent<ResourcesLayout> Cut, NavigationManager Navigation)> RenderOverviewWithTelemetryAsync(Action<IServiceCollection>? configureServices = null)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api-TestId"), ModelTestHelpers.CreateResource("cache-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>());
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client, configureServices: configureServices);
        var requestTime = DateTime.UtcNow.AddSeconds(-1);
        await AddSpanAsync("api", requestTime, "first");
        await AddSpanAsync("cache", requestTime, "first");

        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources/api-TestId");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), "api-TestId");
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".resource-overview")), DefaultWaitTimeout);

        return (cut, navigation);
    }

    private Task AddSpanAsync(string resourceName, DateTime startTime, string spanId) =>
        Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(resourceName),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: $"{resourceName}-{spanId}", spanId: spanId, startTime: startTime, endTime: startTime.AddMilliseconds(5), kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        });
}
