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
    public async Task Selection_QuietReplacementResourceLoadsWithoutNewTelemetry()
    {
        var (cut, navigation) = await RenderOverviewWithTelemetryAsync();
        cut.WaitForAssertion(() => AssertTelemetryLoaded(cut), DefaultWaitTimeout);

        // Hold the next refresh in its delay, without handling cancellation, until the replacement has been selected.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        cut.FindComponent<ResourceOverview>().Instance.TelemetryRefreshDelayAsync = (delay, cancellationToken) =>
        {
            if (Interlocked.Increment(ref delays) == 1)
            {
                holding.SetResult();
                return held.Task;
            }

            return Task.Delay(delay, cancellationToken);
        };
        await AddSpanAsync("api", DateTime.UtcNow, "held");
        await holding.Task.WaitAsync(DefaultWaitTimeout);

        try
        {
            // No more telemetry arrives, so nothing else queues a refresh for the replacement.
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

    private async Task<(IRenderedComponent<ResourcesLayout> Cut, NavigationManager Navigation)> RenderOverviewWithTelemetryAsync()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api-TestId"), ModelTestHelpers.CreateResource("cache-TestId")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>());
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
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
