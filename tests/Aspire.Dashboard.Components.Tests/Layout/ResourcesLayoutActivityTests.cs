// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class ResourcesLayoutActivityTests : DashboardTestContext
{
    [Fact]
    public async Task Canvas_MobileViewportCancelsPendingQueryAndDoesNotResumeOnDesktop()
    {
        var desktop = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var mobile = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var timers = new FakeTimeProvider();
        ResourceSetupHelpers.SetupResourcesLayout(this, desktop,
            new TestDashboardClient(isEnabled: true, initialResources: [ModelTestHelpers.CreateResource("api-TestId")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services =>
            {
                services.AddSingleton<BrowserTimeProvider>(new TestTimeProvider { Timers = timers });
                services.AddSingleton<ITelemetryRepository>(provider => new TestTelemetryRepository(provider.GetRequiredService<SqliteTelemetryRepository>())
                {
                    GetRequestSpansAsyncHandler = async (_, token) =>
                    {
                        Interlocked.Increment(ref calls);
                        started.TrySetResult(token);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return new GetRequestSpansResponse { Items = [], TotalItemCount = 0 };
                    }
                });
            });
        var now = Services.GetRequiredService<BrowserTimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-1);
        await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource("api"),
                ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans =
                {
                    CreateSpan("1", "1", now, now.AddMilliseconds(10), kind: Span.Types.SpanKind.Server)
                } } }
            }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources");
        var cut = Render<CascadingValue<ViewportInformation>>(builder =>
            builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<ResourcesLayout>().Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cut.Render(builder => builder.Add(p => p.Value, mobile).AddChildContent<ResourcesLayout>());
        Assert.True(token.IsCancellationRequested);
        Assert.Empty(cut.FindAll(".resource-row-insight,.pane-full-canvas"));
        cut.Render(builder => builder.Add(p => p.Value, desktop).AddChildContent<ResourcesLayout>());
        timers.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(cut.FindAll(".resource-row-insight,.pane-full-canvas"));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Canvas_UnchangedLogNotificationsDoNotRenderResourceList()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Func<Task>? notifyLogs = null;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: Enumerable.Range(0, 28).Select(i => ModelTestHelpers.CreateResource($"service-{i}-TestId")).ToList(),
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ITelemetryRepository>(provider =>
                new TestTelemetryRepository(provider.GetRequiredService<SqliteTelemetryRepository>())
                {
                    OnNewLogsRegistered = callback => notifyLogs = callback,
                    // Log ingestion also publishes resource notifications. Isolate the log callback's render guard.
                    OnNewResourcesSubscribed = subscription => subscription.Dispose()
                }));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await AddLogAsync(SeverityNumber.Info);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources?pane=tags");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        Assert.NotNull(notifyLogs);
        var before = cut.RenderCount;
        for (var i = 0; i < 28; i++)
        {
            await notifyLogs();
        }
        Assert.Equal(before, cut.RenderCount);
        await AddLogAsync(SeverityNumber.Error);
        cut.WaitForAssertion(() =>
        {
            // The badge links to the resource's error logs, outside the row link so it doesn't select the row.
            var errorLink = cut.Find("[data-resource-name='service-0-TestId'] > a.resource-row-error-link");
            Assert.Equal("1", errorLink.TextContent);
            Assert.Equal("/structuredlogs/resource/service-0-TestId?logLevel=error", errorLink.GetAttribute("href"));
        });
        var afterError = cut.RenderCount;
        await notifyLogs();
        Assert.Equal(afterError, cut.RenderCount);
        repository.MarkViewedErrorLogs(new ResourceKey("service-0", "TestId"));
        await notifyLogs();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".resource-row .error-badge")));

        Task AddLogAsync(SeverityNumber severity) => repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource("service-0"),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(severity: severity) } } }
            }
        });
    }

    [Fact]
    public async Task Canvas_VisibleCadenceExpiresAndResumesWithoutPerMembershipPolling()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var timers = new FakeTimeProvider();
        var clock = new TestTimeProvider { Timers = timers };
        var names = Enumerable.Range(0, 28).Select(i => $"service-{i}").ToArray();
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        var doubleFirst = false;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: names.Select(name => ModelTestHelpers.CreateResource($"{name}-TestId")).ToList(),
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services =>
            {
                services.AddSingleton<BrowserTimeProvider>(clock);
                services.AddSingleton<ITelemetryRepository>(provider =>
                {
                    var inner = provider.GetRequiredService<SqliteTelemetryRepository>();
                    return new TestTelemetryRepository(inner)
                    {
                        GetRequestSpansAsyncHandler = async (request, token) =>
                        {
                            Interlocked.Increment(ref calls);
                            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                            try
                            {
                                Assert.Equal(28, request.ResourceKeys.Count);
                                Assert.Equal(28, request.ResourceKeys.Distinct().Count());
                                var result = await inner.GetRequestSpansAsync(request, token);
                                return doubleFirst
                                    ? new GetRequestSpansResponse
                                    {
                                        Items = [.. result.Items, .. result.Items.Where(span => span.Resource.ResourceKey == new ResourceKey(names[0], "TestId"))],
                                        TotalItemCount = result.TotalItemCount + 20
                                    }
                                    : result;
                            }
                            finally
                            {
                                Interlocked.Decrement(ref active);
                            }
                        }
                    };
                });
            });
        async Task SeedAsync(string prefix, IReadOnlyList<string> seedNames)
        {
            var now = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(),
                new RepeatedField<ResourceSpans>
                {
                    seedNames.Select(name => new ResourceSpans
                    {
                        Resource = CreateResource(name),
                        ScopeSpans =
                        {
                            new ScopeSpans
                            {
                                Scope = CreateScope(),
                                Spans =
                                {
                                    Enumerable.Range(1, 20).Select(i => CreateSpan($"{prefix}-{name}-{i}", $"{prefix}-{name}-{i}",
                                        now, now.AddMilliseconds(i), kind: Span.Types.SpanKind.Server))
                                }
                            }
                        }
                    })
                });
        }
        await SeedAsync("initial", names);
        var tags = Services.GetRequiredService<ResourceTagStore>();
        foreach (var name in names)
        {
            tags.AddTag($"{name}-TestId", "Backend");
            tags.AddTag($"{name}-TestId", "Shared");
        }
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources?pane=tags");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        WaitForInsightCount(2);
        // Child renders finish before the shared refresh's finally releases its coalescing gate.
        await cut.Instance.PaneActivityRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref calls));
        var changedKey = new ResourceKey(names[0], "TestId");
        var changed = cut.FindComponents<ResourcePaneInsights>()
            .Where(component => component.Instance.ResourceKey == changedKey).ToArray();
        var unchanged = cut.FindComponents<ResourcePaneInsights>()
            .Where(component => component.Instance.ResourceKey != changedKey)
            .Select(component => (Component: component, Renders: component.RenderCount,
                Snapshot: cut.Instance.GetPaneActivitySnapshot(component.Instance.ResourceKey))).ToArray();
        doubleFirst = true;
        // Tick the polling timer without changing the displayed clock to isolate
        // a changed resource from the separate renewal of all active URL windows.
        timers.Advance(ResourcesLayout.s_paneActivityRefreshInterval);
        foreach (var component in changed)
        {
            component.WaitForAssertion(() => Assert.Equal("40 incoming", component.Find(".resource-row-insight-requests span").TextContent));
        }
        Assert.Equal(["40 incoming", "40 incoming"],
            cut.FindAll($"[data-resource-name='{names[0]}-TestId'] .resource-row-insight-requests span").Select(span => span.TextContent));
        await cut.Instance.PaneActivityRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.All(unchanged, item =>
        {
            Assert.Equal(item.Renders, item.Component.RenderCount);
            Assert.Same(item.Snapshot, cut.Instance.GetPaneActivitySnapshot(item.Component.Instance.ResourceKey));
        });
        var href = changed[0].Find(".resource-row-insight-requests").GetAttribute("href");
        clock.UtcNow += TimeSpan.FromSeconds(14);
        timers.Advance(TimeSpan.FromSeconds(14));
        await cut.InvokeAsync(() => { });
        Assert.Equal(2, Volatile.Read(ref calls));
        clock.UtcNow += TimeSpan.FromSeconds(1);
        timers.Advance(TimeSpan.FromSeconds(1));
        changed[0].WaitForAssertion(() =>
        {
            Assert.Equal(3, Volatile.Read(ref calls));
            Assert.NotEqual(href, changed[0].Find(".resource-row-insight-requests").GetAttribute("href"));
        });
        await cut.Instance.PaneActivityRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        clock.UtcNow += TimeSpan.FromMinutes(6);
        timers.Advance(ResourcesLayout.s_paneActivityRefreshInterval);
        WaitForInsightCount(0);
        await cut.Instance.PaneActivityRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        var beforeHidden = Volatile.Read(ref calls);
        await cut.InvokeAsync(() => cut.Instance.SetPaneActivityVisible(false));
        doubleFirst = false;
        await SeedAsync("fresh", names);
        clock.UtcNow += TimeSpan.FromSeconds(30);
        timers.Advance(TimeSpan.FromSeconds(30));
        await cut.InvokeAsync(() => { });
        Assert.Equal(beforeHidden, Volatile.Read(ref calls));
        await cut.InvokeAsync(() => cut.Instance.SetPaneActivityVisible(true));
        WaitForInsightCount(2);
        await cut.Instance.PaneActivityRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(beforeHidden + 1, Volatile.Read(ref calls));
        var url = new Uri(new Uri("http://localhost"), cut.Find(".resource-row-insight-requests").GetAttribute("href"));
        var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(QueryHelpers.ParseQuery(url.Query)["filters"].ToString());
        Assert.Equal(clock.GetUtcNow().UtcDateTime.ToString("O"), filters[1].Value);
        await cut.Instance.DisposeAsync();
        timers.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(beforeHidden + 1, Volatile.Read(ref calls));
        Assert.Equal(1, maximumActive);

        void WaitForInsightCount(int count)
        {
            // Activity updates render the insight children, not their layout. Await each child's own render signal.
            var components = cut.FindComponents<ResourcePaneInsights>();
            Assert.Equal(56, components.Count);
            foreach (var component in components)
            {
                component.WaitForAssertion(() => Assert.Equal(count, component.FindAll(".resource-row-insight").Count));
            }
            Assert.Equal(count * components.Count, cut.FindAll(".resource-row-insight").Count);
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 20)]
    public async Task Canvas_SharedBoundedQueryFeedsDuplicateTagRowsAndExactScopedLinks(bool sampled, int sampleCount)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var calls = 0;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api-TestId"),
                ModelTestHelpers.CreateResource("quiet"),
                ModelTestHelpers.CreateResource("key", resourceType: KnownResourceTypes.Parameter)
            ], resourceChannelProvider: () => channel),
            configureServices: services => services.AddSingleton<ITelemetryRepository>(CreateRepository));

        ITelemetryRepository CreateRepository(IServiceProvider services)
        {
            var inner = services.GetRequiredService<SqliteTelemetryRepository>();
            return new TestTelemetryRepository(inner)
            {
                GetRequestSpansAsyncHandler = async (request, token) =>
                {
                    Interlocked.Increment(ref calls);
                    Assert.Equal(TimeSpan.FromMinutes(5), request.EndTime - request.StartTime);
                    Assert.Equal(5000, request.MaxCount);
                    Assert.Equal([new ResourceKey("api", "TestId")], request.ResourceKeys);
                    var result = await inner.GetRequestSpansAsync(request, token);
                    return sampled ? new GetRequestSpansResponse { Items = result.Items, TotalItemCount = 5001 } : result;
                }
            };
        }
        var now = Services.GetRequiredService<BrowserTimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-5);
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
                        Spans =
                        {
                            Enumerable.Range(1, sampleCount).Select(i =>
                                CreateSpan(i.ToString(), i.ToString(), now, now.AddMilliseconds(i),
                                    kind: i == 1 ? Span.Types.SpanKind.Consumer : Span.Types.SpanKind.Server,
                                    status: new Status { Code = i == 1 ? Status.Types.StatusCode.Error : Status.Types.StatusCode.Ok })),
                            CreateSpan("100", "100", now.AddMinutes(-6), now.AddMinutes(-6).AddMilliseconds(100), kind: Span.Types.SpanKind.Server),
                            CreateSpan("101", "101", now, now.AddMilliseconds(50), kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });
        var tags = Services.GetRequiredService<ResourceTagStore>();
        tags.AddTag("api-TestId", "Backend");
        tags.AddTag("api-TestId", "Shared");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/resources?pane=tags");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.Equal(0, Volatile.Read(ref calls));
        ResourceSetupHelpers.ToggleResourceList(cut);

        cut.WaitForAssertion(() => Assert.Equal(sampleCount >= 20 ? 6 : 4, cut.FindAll(".resource-row-insight").Count));
        var expected = sampled ? new[] { "20+ incoming", "p95 19ms", "" } : ["2 incoming", ""];
        Assert.Equal(expected.Concat(expected), cut.FindAll(".resource-row-insight").Select(link => link.TextContent.Trim()));
        Assert.All(cut.FindAll(".resource-row-insight-latency"),
            link => Assert.StartsWith(sampled ? "Sample p95 19ms." : "p95 19ms.", link.GetAttribute("aria-label")));
        Assert.All(cut.FindAll(".resource-row-insight-failed"), link =>
        {
            Assert.StartsWith(sampled ? "1+ failed." : "1 failed.", link.GetAttribute("aria-label"));
            Assert.Single(link.QuerySelectorAll("svg"));
        });
        Assert.All(cut.FindAll(".resource-row-insight"), link =>
        {
            var url = new Uri(new Uri("http://localhost"), link.GetAttribute("href"));
            Assert.Equal("/traces/resource/api-TestId", url.AbsolutePath);
            var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(QueryHelpers.ParseQuery(url.Query)["filters"].ToString());
            Assert.Equal(link.ClassList.Contains("resource-row-insight-failed") ? 3 : 2, filters.Count);
            Assert.Equal(KnownTraceFields.TimestampField, filters[0].Field);
            Assert.Equal(FilterCondition.GreaterThanOrEqual, filters[0].Condition);
            Assert.Equal(FilterCondition.LessThan, filters[1].Condition);
            Assert.Equal(TimeSpan.FromMinutes(5), DateTime.Parse(filters[1].Value) - DateTime.Parse(filters[0].Value));
            Assert.Contains("last 5 minutes", link.GetAttribute("aria-label"));
            Assert.Contains("Refreshed", link.GetAttribute("data-tooltip"));
            Assert.Equal(sampled, link.GetAttribute("data-tooltip")!.Contains("Counts are minimums", StringComparison.Ordinal));
        });
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(cut.FindAll(".resource-row[data-resource-name='quiet'], .resource-row[data-resource-name='key']"),
            row => Assert.Empty(row.QuerySelectorAll(".resource-row-insight")));

        navigation.NavigateTo("/traces/resource/api-TestId");
        cut.Render();
        Assert.Equal("api-TestId", cut.Instance.SelectedResourceName);
        Assert.Equal(ResourceTab.Traces, cut.Instance.CurrentTab);
        Assert.Empty(cut.FindAll(".resource-row-insight"));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Canvas_ReopeningDiscardsOldGenerationAndDisposalCancelsPendingQuery()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken queryToken = default;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport,
            new TestDashboardClient(isEnabled: true, initialResources: [ModelTestHelpers.CreateResource("api-TestId")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ITelemetryRepository>(provider =>
            {
                var inner = provider.GetRequiredService<SqliteTelemetryRepository>();
                return new TestTelemetryRepository(inner)
                {
                    GetRequestSpansAsyncHandler = async (request, token) =>
                    {
                        var result = await inner.GetRequestSpansAsync(request, token);
                        var first = Interlocked.Increment(ref calls) == 1;
                        queryToken = token;
                        (first ? firstStarted : secondStarted).SetResult();
                        await (first ? firstRelease : secondRelease).Task.WaitAsync(token);
                        return result;
                    }
                };
            }));
        var now = Services.GetRequiredService<BrowserTimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-1);
        await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource("api"),
                ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans =
                {
                    CreateSpan("1", "1", now, now.AddMilliseconds(10), kind: Span.Types.SpanKind.Server)
                } } }
            }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources?pane=tags");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(cut.FindAll(".resource-row-insight"));
            ResourceSetupHelpers.ToggleResourceList(cut);
            ResourceSetupHelpers.ToggleResourceList(cut);
            firstRelease.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(cut.FindAll(".resource-row-insight"));
            Assert.Equal(2, Volatile.Read(ref calls));
            await cut.Instance.DisposeAsync();
            Assert.True(queryToken.IsCancellationRequested);
            Assert.Empty(cut.FindAll(".resource-row-insight"));
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canvas_RefreshRemovesExpiredOrFailedData(bool failRefresh)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var clock = new TestTimeProvider();
        var calls = 0;
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport,
            new TestDashboardClient(isEnabled: true, initialResources: [ModelTestHelpers.CreateResource("api-TestId")],
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
                            if (Interlocked.Increment(ref calls) > 1 && failRefresh)
                            {
                                throw new InvalidOperationException("Deterministic activity refresh failure.");
                            }

                            return inner.GetRequestSpansAsync(request, token);
                        }
                    };
                });
            });
        var now = clock.UtcNow.UtcDateTime.AddSeconds(-1);
        async Task SeedAsync(string id)
        {
            await Services.GetRequiredService<SqliteTelemetryRepository>().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource("api"),
                    ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans =
                    {
                        CreateSpan(id, id, now, now.AddMilliseconds(10), kind: Span.Types.SpanKind.Server)
                    } } }
                }
            });
        }

        await SeedAsync("1");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources?pane=tags");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        ResourceSetupHelpers.ToggleResourceList(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".resource-row-insight")));
        clock.UtcNow += TimeSpan.FromMinutes(6);
        await SeedAsync("2");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Empty(cut.FindAll(".resource-row-insight"));
        });
    }
}
