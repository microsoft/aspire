// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Web;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Metrics.V1;
using Aspire.Tests;
using Xunit;
using static Aspire.Dashboard.Components.Pages.Metrics;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class MetricsTests : DashboardTestContext
{
    [Theory]
    [InlineData(AspireDataType.StructuredLogs)]
    [InlineData(AspireDataType.Traces)]
    [InlineData(AspireDataType.Metrics)]
    public async Task EmptyState_HelpDisappearsAfterAnySignalAndStaysHiddenAfterClear(AspireDataType signal)
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var cut = Render<Metrics>(parameters => parameters.AddCascadingValue(viewport));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".telemetry-help-button")));
        Assert.Equal(Resources.Metrics.MetricsSelectAResource, cut.Find(".telemetry-empty-content > div").TextContent.Trim());

        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, signal);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".telemetry-help-button")));
        await repository.ClearStructuredLogsAsync();
        await repository.ClearTracesAsync();
        await repository.ClearMetricsAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".telemetry-empty-content"));
            Assert.Empty(cut.FindAll(".telemetry-help-button"));
        });
    }

    [Fact]
    public async Task EmptyState_SelectedResourceWithoutMetrics_ShowsHelp()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        await Services.GetRequiredService<SqliteTelemetryRepository>().AddMetricsAsync(new(),
            [new ResourceMetrics { Resource = CreateResource("test") }]);
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var cut = Render<Metrics>(parameters => parameters.AddCascadingValue(viewport));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".telemetry-help-button")));
        Assert.Equal(Resources.Metrics.MetricsNoMetricsForResource, cut.Find(".telemetry-empty-content > div").TextContent.Trim());
    }

    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(MetricViewKind.Graph)]
    [InlineData(MetricViewKind.Table)]
    public async Task ChartContainer_NoSharedHistogramBounds_ShowsWarningAndCountRemainsAvailable(MetricViewKind view)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        var timeProvider = new TestTimeProvider { UtcNow = DateTimeOffset.UtcNow };
        Services.AddSingleton<BrowserTimeProvider>(timeProvider);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(timeProvider.UtcNow));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var time = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-3);
        var first = HistogramTestHelpers.CreatePoint(time, time.AddSeconds(1), [3, 0], [10]);
        var second = HistogramTestHelpers.CreatePoint(time, time.AddSeconds(1), [5, 0], [20]);
        first.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue
        {
            Key = "instance",
            Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "first" }
        });
        second.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue
        {
            Key = "instance",
            Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "second" }
        });
        await repository.AddMetricsAsync(new AddContext(),
            [HistogramTestHelpers.CreateMetrics(AggregationTemporality.Delta, first, second)]);
        var resource = Assert.Single(repository.GetResources());
        var cut = Render<ChartContainer>(builder =>
        {
            builder.Add(component => component.ResourceKey, resource.ResourceKey);
            builder.Add(component => component.MeterName, "test-meter");
            builder.Add(component => component.InstrumentName, "histogram");
            builder.Add(component => component.Duration, TimeSpan.FromMinutes(1));
            builder.Add(component => component.ActiveView, view);
            builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowRunningTotal, false);
            builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowChangePerIntervalOption, true);
            builder.Add(component => component.Resources, [resource]);
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                Resources.ControlsStrings.ChartContainerIncompatibleHistogramBounds,
                cut.Find(".block-warning").TextContent.Trim());
            if (view == MetricViewKind.Graph)
            {
                AssertUnavailablePercentiles();
            }
        });

        var instrumentViewModel = view == MetricViewKind.Graph
            ? cut.FindComponent<PlotlyChart>().Instance.InstrumentViewModel
            : cut.FindComponent<MetricTable>().Instance.InstrumentViewModel;
        Assert.True(instrumentViewModel.HasIncompatibleHistogramBounds);
        Assert.False(instrumentViewModel.HasOverflow);

        var filters = cut.FindComponent<ChartFilters>();
        await cut.InvokeAsync(() => filters.Instance.ShowCountChanged.InvokeAsync(true));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                Resources.ControlsStrings.ChartContainerIncompatibleHistogramBounds,
                cut.Find(".block-warning").TextContent.Trim());
            if (view == MetricViewKind.Graph)
            {
                Assert.True(cut.FindComponent<PlotlyChart>().Instance.InstrumentViewModel.ShowCount);
                var invocation = JSInterop.Invocations.Last(i => i.Identifier is "initializeChart" or "updateChart");
                var trace = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PlotlyTrace>>(invocation.Arguments[1]));
                Assert.Equal("Count", trace.Name);
                Assert.Contains(8d, trace.Y);
            }
            else
            {
                Assert.True(cut.FindComponent<MetricTable>().Instance.InstrumentViewModel.ShowCount);
                Assert.Equal(["Time", "Count"], cut.FindComponent<MetricTable>().FindAll("th").Select(column => column.TextContent.Trim()));
            }
        });

        await cut.InvokeAsync(() => filters.Instance.ShowCountChanged.InvokeAsync(false));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                Resources.ControlsStrings.ChartContainerIncompatibleHistogramBounds,
                cut.Find(".block-warning").TextContent.Trim());
            if (view == MetricViewKind.Graph)
            {
                AssertUnavailablePercentiles();
            }
        });

        var instrument = instrumentViewModel.Instrument;
        var dimensions = instrumentViewModel.MatchedDimensions;
        Assert.NotNull(instrument);
        Assert.NotNull(dimensions);

        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(2);
        await cut.InvokeAsync(() => instrumentViewModel.UpdateDataAsync(instrument, dimensions, hasOverflow: true));
        cut.WaitForAssertion(() =>
        {
            Assert.True(instrumentViewModel.HasIncompatibleHistogramBounds);
            Assert.Equal(2, cut.FindAll(".block-warning").Count);
            Assert.Equal(
                Resources.ControlsStrings.ChartContainerOverflowTitle,
                cut.Find(".block-warning .title").TextContent);
        });

        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(2);
        await cut.InvokeAsync(() => instrumentViewModel.UpdateDataAsync(instrument, dimensions, hasOverflow: false));
        cut.WaitForAssertion(() => Assert.Equal(
            Resources.ControlsStrings.ChartContainerIncompatibleHistogramBounds,
            Assert.Single(cut.FindAll(".block-warning")).TextContent.Trim()));

        void AssertUnavailablePercentiles()
        {
            var invocation = JSInterop.Invocations.Last(i => i.Identifier is "initializeChart" or "updateChart");
            var traces = Assert.IsAssignableFrom<IEnumerable<PlotlyTrace>>(invocation.Arguments[1]).ToArray();
            Assert.Equal(3, traces.Length);
            Assert.All(traces, trace =>
            {
                Assert.Equal(32, trace.Y.Count);
                Assert.Equal(trace.X.Count, trace.Y.Count);
                Assert.All(trace.Y, Assert.Null);
            });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChartContainer_UnavailableHistogramPercentiles_TablePreservesExemplars(bool includeAvailableIntervals, bool onlyShowValueChanges)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true).SetVoidResult();
        var timeProvider = new TestTimeProvider { UtcNow = DateTimeOffset.UtcNow };
        Services.AddSingleton<BrowserTimeProvider>(timeProvider);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(timeProvider.UtcNow));
        var dialogs = new ConcurrentQueue<ExemplarsDialogViewModel>();
        Services.AddSingleton<IDialogService>(new TestDialogService((content, _) =>
        {
            dialogs.Enqueue(Assert.IsType<ExemplarsDialogViewModel>(content));
            return Task.CompletedTask;
        }));
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var time = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-5);
        var points = new List<HistogramDataPoint>();
        if (includeAvailableIntervals)
        {
            AddPoints(time.AddSeconds(-2), compatibleBounds: true, exemplarValue: 1);
        }
        AddPoints(time, compatibleBounds: false, exemplarValue: 2);
        if (includeAvailableIntervals)
        {
            AddPoints(time.AddSeconds(2), compatibleBounds: true, exemplarValue: 3);
        }
        var addContext = new AddContext();
        await repository.AddMetricsAsync(addContext, [HistogramTestHelpers.CreateMetrics(AggregationTemporality.Delta, [.. points])]);
        Assert.Equal(0, addContext.FailureCount);

        var resource = Assert.Single(repository.GetResources());
        var cut = Render<ChartContainer>(builder =>
        {
            builder.Add(component => component.ResourceKey, resource.ResourceKey);
            builder.Add(component => component.MeterName, "test-meter");
            builder.Add(component => component.InstrumentName, "histogram");
            builder.Add(component => component.Duration, TimeSpan.FromMinutes(1));
            builder.Add(component => component.ActiveView, MetricViewKind.Table);
            builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowRunningTotal, false);
            builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowChangePerIntervalOption, true);
            builder.Add(component => component.Resources, [resource]);
        });
        cut.WaitForState(() => cut.FindComponents<MetricTable>().Count == 1);
        var table = cut.FindComponent<MetricTable>();
        var instrumentViewModel = table.Instance.InstrumentViewModel;
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(instrumentViewModel.Instrument);
            Assert.NotNull(instrumentViewModel.MatchedDimensions);
        });
        await cut.InvokeAsync(async () =>
        {
            table.Instance.OnlyShowValueChangesInTable = onlyShowValueChanges;
            timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(3);
            await instrumentViewModel.UpdateDataAsync(instrumentViewModel.Instrument!, instrumentViewModel.MatchedDimensions!, hasOverflow: false);
        });

        // Two-second chart windows are aligned one second behind the live clock.
        var expectedTime = FormatHelpers.FormatTimeWithOptionalDate(timeProvider, timeProvider.ToLocal(new DateTimeOffset(time.AddSeconds(2))));
        cut.WaitForAssertion(() =>
        {
            Assert.True(instrumentViewModel.HasIncompatibleHistogramBounds);
            Assert.Equal(includeAvailableIntervals ? ["1", "2", "1"] : ["2"],
                table.FindAll("fluent-button").Select(button => button.TextContent.Trim()));
            Assert.Equal((includeAvailableIntervals ? 3 : 1) * 5, table.FindAll("[role='gridcell']").Count);
            var button = Assert.Single(table.FindAll("fluent-button"), button => button.TextContent.Trim() == "2");
            var row = button.Closest("[role='row']");
            Assert.NotNull(row);
            Assert.Equal([expectedTime, "", "", "", "2"], row.QuerySelectorAll("[role='gridcell']").Select(cell => cell.TextContent.Trim()));
            Assert.All(row.QuerySelectorAll("[role='gridcell']").Skip(1).Take(3),
                cell => Assert.Equal("empty-data", Assert.Single(cell.Children).ClassName));
        });

        table.FindAll("fluent-button").Single(button => button.TextContent.Trim() == "2").Click();
        var dialog = Assert.Single(dialogs);
        Assert.Same(instrumentViewModel.Instrument, dialog.Instrument);
        Assert.Equal([2d, 22d], dialog.Exemplars.Select(exemplar => exemplar.Value).Order());
        var expectedExemplarTime = timeProvider.ToLocalDateTimeOffset(new DateTimeOffset(time.AddMilliseconds(500)));
        Assert.All(dialog.Exemplars, exemplar => Assert.Equal(expectedExemplarTime, exemplar.Start));

        void AddPoints(DateTime start, bool compatibleBounds, double exemplarValue)
        {
            var first = HistogramTestHelpers.CreatePoint(start, start.AddSeconds(1), [3, 0], [10]);
            var second = HistogramTestHelpers.CreatePoint(start, start.AddSeconds(1), [5, 0], compatibleBounds ? [10] : [20]);
            first.Exemplars.Add(CreateExemplar(start.AddMilliseconds(500), exemplarValue));
            if (!compatibleBounds)
            {
                second.Exemplars.Add(CreateExemplar(start.AddMilliseconds(500), exemplarValue + 20));
            }
            foreach (var (point, instance) in new[] { (first, "first"), (second, "second") })
            {
                point.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue
                {
                    Key = "instance",
                    Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = instance }
                });
                points.Add(point);
            }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SignalActions_UseTelemetryRepositoryReadOnlyState(bool telemetryRepositoryIsReadOnly, bool dashboardClientIsReadOnly)
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isReadOnly: dashboardClientIsReadOnly));
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, telemetryRepositoryIsReadOnly, _ => Task.CompletedTask);

        var cut = Render<Metrics>(builder => builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        Assert.Equal(telemetryRepositoryIsReadOnly, cut.FindComponent<PauseIncomingDataSwitch>().Instance.Disabled);
        Assert.Equal(telemetryRepositoryIsReadOnly, cut.FindComponent<ClearSignalsButton>().FindComponent<AspireMenuButton>().Instance.Disabled);
    }

    [Fact]
    public void ResourcesLayout_HidesResourceSelect()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        MetricsSetupHelpers.SetupMetricsPage(this);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: false));
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.MetricsUrl());
        Render<FluentToastProvider>();

        var cut = Render<ResourcesLayout>(builder =>
        {
            builder.AddCascadingValue(viewport);
            builder.Add(layout => layout.Body, bodyBuilder =>
            {
                bodyBuilder.OpenComponent<Metrics>(0);
                bodyBuilder.CloseComponent();
            });
        });

        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<ResourceSelect>()));
    }

    [Fact]
    public async Task ChangeResource_MeterAndInstrumentOnNewResource_InstrumentSet()
    {
        await ChangeResourceAndAssertInstrument(
            app1InstrumentName: "test1",
            app2InstrumentName: "test1",
            expectedMeterNameAfterChange: "test-meter",
            expectedInstrumentNameAfterChange: "test1");
    }

    [Fact]
    public async Task ChangeResource_MeterAndInstrumentNotOnNewResource_MeterOverviewSelected()
    {
        await ChangeResourceAndAssertInstrument(
            app1InstrumentName: "test1",
            app2InstrumentName: "test2",
            expectedMeterNameAfterChange: "test-meter",
            expectedInstrumentNameAfterChange: null);
    }

    [Fact]
    public async Task ChartContainer_ParametersAndActiveView_OnlyRefreshAndRenderWhenChanged()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        var chartTimeProvider = new TestTimeProvider { UtcNow = DateTimeOffset.UtcNow };
        Services.AddSingleton<BrowserTimeProvider>(chartTimeProvider);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var metricTime = chartTimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(
                                metricName: "test-instrument",
                                startTime: metricTime,
                                attributes: [new KeyValuePair<string, string>("http.method", "GET")])
                        }
                    }
                }
            }
        });

        var resource = telemetryRepository.GetResources().Single();
        var cut = Render<ChartContainer>(builder =>
        {
            builder.Add(component => component.ResourceKey, resource.ResourceKey);
            builder.Add(component => component.MeterName, "test-meter");
            builder.Add(component => component.InstrumentName, "test-instrument");
            builder.Add(component => component.Duration, TimeSpan.FromMinutes(5));
            builder.Add(component => component.ActiveView, MetricViewKind.Graph);
            builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
            // This test checks table rows and formatting, so show the counter's reported values, not its change per interval.
            builder.Add(component => component.ShowRunningTotal, true);
            builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowChangePerIntervalOption, true);
            builder.Add(component => component.Resources, [resource]);
        });
        cut.WaitForAssertion(() =>
        {
            var dimensionFilter = Assert.Single(cut.Instance.DimensionFilters);
            Assert.Equal("GET", Assert.Single(dimensionFilter.SelectedValues).Value);
            Assert.Single(cut.FindComponents<PlotlyChart>());
            Assert.Empty(cut.FindComponents<MetricTable>());
        });
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetryRepository.SqlActivitySource, onActivityStopped: activities.Enqueue);

        cut.Render(builder => builder.Add(component => component.ActiveView, MetricViewKind.Table));

        Assert.Empty(cut.FindComponents<PlotlyChart>());
        Assert.Single(cut.FindComponents<MetricTable>());
        if (cut.FindAll(".empty-content-cell") is [var emptyContentCell])
        {
            Assert.Equal("Loading...", emptyContentCell.TextContent.Trim());
        }
        cut.WaitForAssertion(() => Assert.Equal("1",
            cut.Find("[role='gridcell']:has([aria-label='Value increased'])").TextContent.Trim()));
        Assert.Empty(activities);

        cut.Render(builder => builder.Add(component => component.ActiveView, MetricViewKind.Graph));

        Assert.Single(cut.FindComponents<PlotlyChart>());
        Assert.Empty(cut.FindComponents<MetricTable>());
        Assert.Empty(activities);

        var chartFilters = cut.FindComponent<ChartFilters>().Instance;
        var filterPopover = cut.FindComponent<ChartFilterPopover>().Instance;
        var filterButtonId = cut.Find(".chart-filter-button").Id;
        cut.Find(".chart-filter-button").Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("fluent-popover-b.chart-filter-popover").GetAttribute("opened")));

        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(
                                metricName: "test-instrument",
                                startTime: metricTime,
                                attributes: [new KeyValuePair<string, string>("http.method", "POST")])
                        }
                    }
                }
            }
        });
        activities.Clear();

        cut.Render(builder => builder.Add(component => component.Duration, TimeSpan.FromMinutes(5)));

        Assert.Empty(activities);

        cut.Render(builder => builder.Add(component => component.Duration, TimeSpan.FromMinutes(1)));

        cut.WaitForAssertion(() =>
        {
            var updatedFilter = Assert.Single(cut.Instance.DimensionFilters);
            Assert.Same(chartFilters, cut.FindComponent<ChartFilters>().Instance);
            Assert.Same(filterPopover, cut.FindComponent<ChartFilterPopover>().Instance);
            Assert.Equal(filterButtonId, cut.Find(".chart-filter-button").Id);
            Assert.Equal("true", cut.Find("fluent-popover-b.chart-filter-popover").GetAttribute("opened"));
            Assert.Collection(
                updatedFilter.SelectedValues.Select(value => value.Value).Order(),
                value => Assert.Equal("GET", value),
                value => Assert.Equal("POST", value));

            var chart = cut.FindComponent<PlotlyChart>();
            var dimensions = chart.Instance.InstrumentViewModel.MatchedDimensions!;
            Assert.Equal(2, dimensions.Count);
            Assert.All(dimensions, dimension => Assert.Equal(1, Assert.IsType<MetricValue<long>>(Assert.Single(dimension.Values)).Value));
            Assert.Contains(activities, activity => ((string?)activity.GetTagItem("db.query.text"))?.Contains("telemetry_metric_points", StringComparison.Ordinal) == true);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChartContainer_Counter_ShowsChangePerIntervalOptionOnlyWhenOffered(bool showOption)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly: true, telemetryRepository => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "instrument-1", startTime: s_testTime.AddMinutes(1), value: 0),
                            CreateSumMetric(metricName: "instrument-1", startTime: s_testTime.AddMinutes(2), value: 1)
                        }
                    }
                }
            }
        }));

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var resource = telemetryRepository.GetResources().Single();

        // The previous experience passes the running total without the option, which is how it showed counters before.
        var cut = Render<ChartContainer>(builder =>
        {
            builder.Add(component => component.ResourceKey, resource.ResourceKey);
            builder.Add(component => component.MeterName, "test-meter");
            builder.Add(component => component.InstrumentName, "instrument-1");
            builder.Add(component => component.Duration, TimeSpan.FromMinutes(5));
            builder.Add(component => component.ActiveView, MetricViewKind.Table);
            builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowRunningTotal, !showOption);
            builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowChangePerIntervalOption, showOption);
            builder.Add(component => component.Resources, [resource]);
        });

        cut.WaitForState(() => cut.FindComponents<ChartFilters>().Count == 1);
        Assert.Equal(showOption, cut.FindComponent<ChartFilters>().Instance.IsCounter);
        Assert.Equal(showOption ? 1 : 0, cut.FindAll(".counter-change-per-interval-switch").Count);
        Assert.Equal(!showOption, cut.FindComponent<MetricTable>().Instance.InstrumentViewModel.ShowRunningTotal);
    }

    [Fact]
    public async Task ChartContainer_InstrumentChanged_TableUpdates()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly: true, telemetryRepository => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "instrument-1", startTime: s_testTime.AddMinutes(1), value: 0),
                            CreateSumMetric(metricName: "instrument-1", startTime: s_testTime.AddMinutes(2), value: 1),
                            CreateSumMetric(metricName: "instrument-2", startTime: s_testTime.AddMinutes(1), value: 0),
                            CreateSumMetric(metricName: "instrument-2", startTime: s_testTime.AddMinutes(2), value: 2)
                        }
                    }
                }
            }
        }));

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var resource = telemetryRepository.GetResources().Single();
        var cut = Render<ChartContainer>(builder =>
        {
            builder.Add(component => component.ResourceKey, resource.ResourceKey);
            builder.Add(component => component.MeterName, "test-meter");
            builder.Add(component => component.InstrumentName, "instrument-1");
            builder.Add(component => component.Duration, TimeSpan.FromMinutes(5));
            builder.Add(component => component.ActiveView, MetricViewKind.Table);
            builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
            // This test checks table rows and formatting, so show the counter's reported values, not its change per interval.
            builder.Add(component => component.ShowRunningTotal, true);
            builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
            builder.Add(component => component.ShowChangePerIntervalOption, true);
            builder.Add(component => component.Resources, [resource]);
        });

        cut.WaitForState(() => cut.FindComponents<MetricTable>().Count == 1);
        var table = cut.FindComponent<MetricTable>();
        table.Render();
        table.WaitForAssertion(() => Assert.Equal("1",
            table.Find("[role='gridcell']:has([aria-label='Value increased'])").TextContent.Trim()));

        cut.Render(builder => builder.Add(component => component.InstrumentName, "instrument-2"));

        cut.WaitForAssertion(() =>
        {
            var updatedTable = cut.FindComponent<MetricTable>();
            Assert.Equal("instrument-2", updatedTable.Instance.InstrumentViewModel.Instrument?.Name);
            Assert.NotEmpty(updatedTable.Instance.InstrumentViewModel.MatchedDimensions!);
        });
        cut.WaitForAssertion(() => Assert.Equal("2",
            cut.Find("[role='gridcell']:has([aria-label='Value increased'])").TextContent.Trim()));
    }

    [Fact]
    public async Task ChartContainer_TickUpdate_PreservesComponentCultureAndStopsOnDispose()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        MetricsSetupHelpers.SetupMetricsPage(this);
        var timeProvider = new SignalingFakeTimeProvider();
        Services.AddSingleton<TimeProvider>(timeProvider);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var metric = CreateSumMetric(metricName: "test-instrument", startTime: DateTime.UtcNow);
        // End in the current SQLite query window while spanning the fixed browser time used by the rendered chart.
        metric.Sum.DataPoints.Single().StartTimeUnixNano = 0;
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            metric
                        }
                    }
                }
            }
        });
        var resource = telemetryRepository.GetResources().Single();

        var activitySource = Services.GetRequiredService<DashboardActivitySource>();
        var tickActivityCount = 0;
        var activityStarted = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityStopped = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = ActivityListenerHelper.Create(
            activitySource.ActivitySource,
            onActivityStarted: activity =>
            {
                if (activity.OperationName == "Update metric chart data from tick")
                {
                    Interlocked.Increment(ref tickActivityCount);
                    activityStarted.TrySetResult(activity);
                }
            },
            onActivityStopped: activity =>
            {
                if (activity.OperationName == "Update metric chart data from tick")
                {
                    activityStopped.TrySetResult(activity);
                }
            });

        var componentCulture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        componentCulture.DateTimeFormat.AMDesignator = "am";
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = componentCulture;
            var cut = Render<ChartContainer>(builder =>
            {
                builder.Add(component => component.ResourceKey, resource.ResourceKey);
                builder.Add(component => component.MeterName, "test-meter");
                builder.Add(component => component.InstrumentName, "test-instrument");
                builder.Add(component => component.Duration, TimeSpan.FromSeconds(1));
                builder.Add(component => component.ActiveView, MetricViewKind.Table);
                builder.Add(component => component.OnViewChangedAsync, _ => Task.CompletedTask);
                // This test checks table rows and formatting, so show the counter's reported values, not its change per interval.
                builder.Add(component => component.ShowRunningTotal, true);
                builder.Add(component => component.OnShowRunningTotalChangedAsync, _ => Task.CompletedTask);
                builder.Add(component => component.ShowChangePerIntervalOption, true);
                builder.Add(component => component.Resources, [resource]);
            });

            cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<MetricTable>()));
            await timeProvider.TimerCreated.Task.DefaultTimeout();
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            var started = await activityStarted.Task.WaitAsync(DefaultWaitTimeout);
            var activity = await activityStopped.Task.WaitAsync(DefaultWaitTimeout);

            Assert.Same(started, activity);
            Assert.Equal(ActivityKind.Internal, activity.Kind);
            Assert.Null(activity.ParentId);
            cut.WaitForAssertion(() =>
            {
                Assert.Equal("12:59:57 am", cut.Find("[role='gridcell']").TextContent.Trim(), ignoreWhiteSpaceDifferences: true);
            });

            await cut.Instance.DisposeAsync().DefaultTimeout();
            var activityCountAfterDispose = Volatile.Read(ref tickActivityCount);
            timeProvider.Advance(TimeSpan.FromSeconds(10));

            Assert.Equal(activityCountAfterDispose, Volatile.Read(ref tickActivityCount));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task InitialLoad_SingleResource_RedirectToResource()
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();

        var targetLocationTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var changeHandler = navigationManager.RegisterLocationChangingHandler(c =>
        {
            targetLocationTcs.SetResult(c.TargetLocation);
            return ValueTask.CompletedTask;
        });

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        var cut = Render<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        Assert.NotNull(targetLocationTcs);
        Assert.Equal("/metrics/resource/TestApp?duration=5", await targetLocationTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task InitialLoad_HasSessionState_RedirectUsingState()
    {
        // Arrange
        var testSessionStorage = new TestSessionStorage
        {
            OnGetAsync = key =>
            {
                if (key == BrowserStorageKeys.MetricsPageState)
                {
                    var state = new MetricsPageState
                    {
                        ResourceName = "TestApp2",
                        MeterName = "test-meter",
                        InstrumentName = "test-instrument",
                        DurationMinutes = 720,
                        ViewKind = MetricViewKind.Table.ToString()
                    };
                    return (true, state);
                }
                else if (key == BrowserStorageKeys.MetricsOrganize)
                {
                    return (false, null);
                }
                else
                {
                    throw new InvalidOperationException("Unexpected key: " + key);
                }
            }
        };

        MetricsSetupHelpers.SetupMetricsPage(this, sessionStorage: testSessionStorage);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl());

        Uri? loadRedirect = null;
        navigationManager.LocationChanged += (s, a) =>
        {
            loadRedirect = new Uri(a.Location);
        };

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        var cut = Render<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        Assert.NotNull(loadRedirect);
        Assert.Equal("/metrics/resource/TestApp2", loadRedirect.AbsolutePath);

        var query = HttpUtility.ParseQueryString(loadRedirect.Query);
        Assert.Equal("test-meter", query["meter"]);
        Assert.Equal("test-instrument", query["instrument"]);
        Assert.Equal("720", query["duration"]);
        Assert.Equal(MetricViewKind.Table.ToString(), query["view"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-instrument")]
    public async Task InitialLoad_RouteSelection_SelectsMeterGroup(string? instrumentName)
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(
            resource: "TestApp",
            meter: "test-meter",
            instrument: instrumentName,
            duration: 5,
            view: MetricViewKind.Table.ToString()));

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(component => component.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() =>
        {
            // The meter's group is selected whether the route opens the meter's cards or one of its instruments.
            var selectedGroup = Assert.Single(cut.FindAll(".metric-group.selected"));
            Assert.Equal("test-meter", selectedGroup.QuerySelector(".metric-group-name")!.TextContent);
            Assert.Equal("page", selectedGroup.GetAttribute("aria-current"));
            Assert.Equal("test-meter", cut.Instance.PageViewModel.SelectedMeter);
            Assert.Equal(instrumentName, cut.Instance.PageViewModel.SelectedInstrument?.Name);
        });
    }

    [Fact]
    public async Task PauseIncomingData_DisplaysPauseWarningInPageFooter()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: "test-instrument"));

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(viewport);
        });

        cut.WaitForState(() => cut.Instance.PageViewModel.SelectedInstrument is not null);

        var pauseManager = Services.GetRequiredService<PauseManager>();
        var timeProvider = Services.GetRequiredService<BrowserTimeProvider>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.Metrics>>();

        cut.FindComponent<PauseIncomingDataSwitch>().WaitForElement("fluent-button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(pauseManager.AreMetricsPaused(out var pausedAt));
            var pauseWarning = cut.FindComponent<PauseWarning>();
            Assert.Equal(
                string.Format(
                    CultureInfo.CurrentCulture,
                    loc[nameof(Resources.Metrics.PauseInProgressText)],
                    FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pausedAt.Value)),
                pauseWarning.Instance.PauseText);
            Assert.Single(cut.Find("footer").QuerySelectorAll(".block-warning"));
        });

        cut.FindComponent<PauseIncomingDataSwitch>().WaitForElement("fluent-button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(pauseManager.AreMetricsPaused(out _));
            Assert.False(cut.HasComponent<PauseWarning>());
        });
    }

    [Fact]
    public async Task MetricGroups_MetricsAdded_GroupsUpdated()
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument1-1", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act 1
        // Initial page load
        var cut = Render<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(m => m.ResourceName, "TestApp");
        });

        // Assert 2
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 1);

        // Assert in wait to make sure rendering has caught up to data update.
        cut.WaitForAssertion(() => Assert.Equal(["test-meter1 1"], GetMetricGroups(cut)));

        // Act 2
        // New instruments added
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument1-2", startTime: s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument2-1", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Assert 2
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 3);

        cut.WaitForAssertion(() => Assert.Equal(["test-meter1 2", "test-meter2 1"], GetMetricGroups(cut)));
    }

    [Fact]
    public async Task MetricGroups_TextFilter_ShowsMatchesFromEveryMeter()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "http.request.count", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "memory.usage", startTime: s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "queue.length", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(m => m.ResourceName, "TestApp");
        });

        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 3);

        // Show the first meter's cards, so the test covers matches outside the selected group.
        cut.FindAll(".metric-group").Single(g => g.TextContent.Contains("test-meter1")).Click();
        cut.WaitForAssertion(() => Assert.Equal(["http.request.count", "memory.usage"], GetCardNames(cut)));

        var filterInput = cut.FindComponents<FluentTextInput>().Single(c => c.Instance.Class == "metric-groups-search");

        // Typing shows matches from every meter, grouped by meter, and no group is selected.
        await cut.InvokeAsync(() => filterInput.Instance.ValueChanged.InvokeAsync("MEMORY"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["test-meter1 1"], GetMetricGroups(cut));
            Assert.Equal(["test-meter1: memory.usage"], GetFilterResults(cut));
            Assert.Empty(cut.FindAll(".metric-group.selected"));
        });

        // A match in another meter than the selected one is shown too.
        await cut.InvokeAsync(() => filterInput.Instance.ValueChanged.InvokeAsync("e"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["test-meter1 2", "test-meter2 1"], GetMetricGroups(cut));
            Assert.Equal(["test-meter1: http.request.count", "test-meter1: memory.usage", "test-meter2: queue.length"], GetFilterResults(cut));
        });

        // Picking a group narrows the matches to that group.
        cut.FindAll(".metric-group").Single(g => g.TextContent.Contains("test-meter2")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".metrics-section-title"));
            Assert.Equal(["queue.length"], GetCardNames(cut));
            Assert.Equal("test-meter2", cut.Find(".metric-group.selected .metric-group-name").TextContent);
        });

        await cut.InvokeAsync(() => filterInput.Instance.ValueChanged.InvokeAsync("does-not-exist"));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(GetMetricGroups(cut));
            Assert.Equal("No metrics match the filter", cut.Find(".metric-groups-empty").TextContent.Trim());
        });

        // Clearing the filter returns to the selected group.
        await cut.InvokeAsync(() => filterInput.Instance.ValueChanged.InvokeAsync(""));
        cut.WaitForAssertion(() => Assert.Equal(["queue.length"], GetCardNames(cut)));

        // Each filter result as "<meter>: <instrument>".
        static List<string> GetFilterResults(IRenderedComponent<Metrics> cut) =>
            [.. cut.FindAll(".metrics-filter-results-group").SelectMany(g => g.QuerySelectorAll(".meter-card-name")
                .Select(n => $"{g.QuerySelector(".metrics-section-title")!.TextContent}: {n.TextContent}"))];
    }

    [Fact]
    public async Task ReadOnly_ChartEndsAtLatestMetricTime()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly: true, telemetryRepository => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(2))
                        }
                    }
                }
            }
        }));
        Services.GetRequiredService<PauseManager>().SetMetricsPaused(true);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: "test-instrument", duration: 5, view: MetricViewKind.Graph.ToString()));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<PlotlyChart>()));
        var chart = cut.FindComponent<PlotlyChart>().Instance;
        var expectedEndTime = new DateTimeOffset(s_testTime.AddMinutes(2));
        Assert.Equal(expectedEndTime, chart.DataEndTime);
        cut.WaitForAssertion(() =>
        {
            var initializeChart = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "initializeChart");
            Assert.Equal(chart.TimeProvider.ToLocal(expectedEndTime), Assert.IsType<DateTime>(initializeChart.Arguments[3]));
        });
    }

    [Fact]
    public async Task MeterSelected_ShowsInstrumentCards()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        // Read-only telemetry anchors each card to the instrument's latest data, so the
        // historical test timestamps fall inside the card's time range.
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly: true, telemetryRepository => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateCumulativeSumMetric(metricName: "test-instrument-b", isMonotonic: true, time: s_testTime.AddMinutes(1), value: 3),
                            CreateCumulativeSumMetric(metricName: "test-instrument-b", isMonotonic: true, time: s_testTime.AddMinutes(2), value: 7),
                            CreateCumulativeSumMetric(metricName: "test-instrument-a", isMonotonic: true, time: s_testTime.AddMinutes(1), value: 1),
                            CreateCumulativeSumMetric(metricName: "test-instrument-a", isMonotonic: true, time: s_testTime.AddMinutes(2), value: 2),
                            CreateCumulativeSumMetric(metricName: "test-instrument-c", isMonotonic: false, time: s_testTime.AddMinutes(1), value: 4),
                            CreateCumulativeSumMetric(metricName: "test-instrument-c", isMonotonic: false, time: s_testTime.AddMinutes(2), value: 2)
                        }
                    }
                }
            }
        }));
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", duration: 5, view: MetricViewKind.Graph.ToString()));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll(".meter-card");
            Assert.Collection(cards,
                card => AssertCard(card, "test-instrument-a", sparklineClass: "metric-sparkline-bars"),
                card => AssertCard(card, "test-instrument-b", sparklineClass: "metric-sparkline-bars"),
                card => AssertCard(card, "test-instrument-c", sparklineClass: "metric-sparkline-line"));
        });

        // Counters (monotonic sums) show bars of the increase per interval; up-down counters show a line.
        static void AssertCard(AngleSharp.Dom.IElement card, string instrumentName, string sparklineClass)
        {
            Assert.Equal(instrumentName, card.QuerySelector(".meter-card-name")!.TextContent);
            Assert.Equal(
                DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: instrumentName, duration: 5, view: MetricViewKind.Graph.ToString()),
                card.QuerySelector(".meter-card-link")!.GetAttribute("href"));
            Assert.NotNull(card.QuerySelector(".meter-card-value-number"));
            Assert.False(string.IsNullOrEmpty(card.QuerySelector($".{sparklineClass}")!.GetAttribute("d")));
        }

        // Cumulative points share the process start time and advance their timestamp, like a real
        // exporter. That lets the overview see the running total grow between intervals.
        static Metric CreateCumulativeSumMetric(string metricName, bool isMonotonic, DateTime time, int value)
        {
            return new Metric
            {
                Name = metricName,
                Unit = "widget",
                Sum = new Sum
                {
                    AggregationTemporality = AggregationTemporality.Cumulative,
                    IsMonotonic = isMonotonic,
                    DataPoints =
                    {
                        new NumberDataPoint
                        {
                            AsInt = value,
                            StartTimeUnixNano = TelemetryTestHelpers.DateTimeToUnixNanoseconds(s_testTime),
                            TimeUnixNano = TelemetryTestHelpers.DateTimeToUnixNanoseconds(time)
                        }
                    }
                }
            };
        }
    }

    [Fact]
    public async Task InitialLoad_RouteResourceArrivesLater_ResolvesRoute()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();

        // Another resource exists up front so the page doesn't auto-select the only resource,
        // which would hide whether the route itself is resolved.
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "OtherApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "other-meter"),
                        Metrics = { CreateSumMetric(metricName: "other-instrument", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        Services.GetRequiredService<NavigationManager>().NavigateTo(
            DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: "test-instrument", duration: 5, view: MetricViewKind.Graph.ToString()));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // The route's resource has no telemetry yet, so it can't be resolved.
        cut.WaitForAssertion(() => Assert.Null(cut.Instance.PageViewModel.SelectedResource.Id));

        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        cut.WaitForAssertion(() =>
        {
            var viewModel = cut.Instance.PageViewModel;
            Assert.Equal("TestApp", viewModel.SelectedResource.Name);
            Assert.Equal("test-meter", viewModel.SelectedMeter);
            Assert.Equal("test-instrument", viewModel.SelectedInstrument?.Name);
        });
    }

    [Fact]
    public async Task MeterCard_PinInstrument_ShowsInPinnedSection()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument-a", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test-instrument-b", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", duration: 5, view: MetricViewKind.Graph.ToString()));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() => Assert.Equal(["test-meter 2"], GetMetricGroups(cut)));

        var pinButton = cut.FindComponents<MetricPinButton>().Single(b => b.Instance.InstrumentKey.InstrumentName == "test-instrument-b");
        pinButton.Find("fluent-button").Click();

        // The pinned group appears above the meters, linking to its own URL.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Pinned 1", "test-meter 2"], GetMetricGroups(cut));
            Assert.Equal(
                DashboardUrls.MetricsUrl(resource: "TestApp", duration: 5, view: MetricViewKind.Graph.ToString(), pinned: true),
                cut.Find(".metric-group.pinned").GetAttribute("href"));
            Assert.Equal("true", pinButton.Find("fluent-button").GetAttribute("aria-pressed"));
        });
        Assert.Equal([new OtlpInstrumentKey("test-meter", "test-instrument-b")], Services.GetRequiredService<MetricPinStore>().GetPins());

        // Selecting the pinned group shows the pinned instruments as cards, grouped by their meter.
        cut.Find(".metric-group.pinned").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.PageViewModel.ShowPinned);
            Assert.Equal(["test-instrument-b"], GetCardNames(cut));
            Assert.Equal(["test-meter"], cut.FindAll(".metrics-section-title").Select(t => t.TextContent));
        });

        // Unpinning the last pin keeps the selected group, explaining how to pin instead of an empty pane.
        cut.FindComponents<MetricPinButton>().Single(b => b.Instance.InstrumentKey.InstrumentName == "test-instrument-b").Find("fluent-button").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Pinned 0", "test-meter 2"], GetMetricGroups(cut));
            Assert.NotNull(cut.Find(".metrics-pinned-empty"));
        });
    }

    [Fact]
    public async Task InitialLoad_WithPinnedInstruments_ShowsPinnedGroup()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        Services.GetRequiredService<MetricPinStore>().SetPinned(new OtlpInstrumentKey("test-meter", "test-instrument-b"), pinned: true);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument-a", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test-instrument-b", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // With no meter or instrument in the route, the page opens on the instruments the user chose to watch.
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.PageViewModel.ShowPinned);
            Assert.Equal("Pinned", cut.Find(".metric-group.selected .metric-group-name").TextContent);
            Assert.Equal(["test-instrument-b"], GetCardNames(cut));
        });
    }

    [Fact]
    public async Task LiveMetricTiles_ShowsLatestValueAndSparkline()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        // Read-only telemetry anchors each sparkline to the instrument's latest data, so the
        // historical test timestamps fall inside the sparkline's time range.
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly: true, telemetryRepository => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1), value: 12345)
                        }
                    }
                }
            }
        }));

        var repository = Services.GetRequiredService<DashboardDataSource>().TelemetryRepository;
        var resource = Assert.Single(repository.GetResources());
        var instruments = repository.GetInstrumentSummaries(resource.ResourceKey);

        var cut = Render<LiveMetricTiles>(builder =>
        {
            builder.Add(c => c.ResourceKey, resource.ResourceKey);
            builder.Add(c => c.Instruments, instruments);
            builder.Add(c => c.Duration, TimeSpan.FromMinutes(5));
            builder.Add(c => c.GetInstrumentUrl, instrument => $"/metrics/{instrument.Name}");
        });

        cut.WaitForAssertion(() =>
        {
            var tile = Assert.Single(cut.FindAll(".overview-metric-tile"));
            Assert.Equal("/metrics/test-instrument", tile.GetAttribute("href"));
            Assert.Equal("test-instrument", tile.QuerySelector(".overview-metric-name")!.TextContent);
            // The tile doesn't show the meter, to stay compact; its tooltip names it.
            Assert.Equal("test-meter \u203A test-instrument \u2014 Test metric description", tile.GetAttribute("data-tooltip"));
            Assert.Equal("12.3K", tile.QuerySelector(".live-metric-tile-number")!.TextContent);
            // A counter's sparkline shows bars of the increase per interval.
            Assert.NotNull(tile.QuerySelector(".metric-sparkline-bars"));
        });
    }

    [Fact]
    public async Task MeterOverview_VisibleCardsReported_RefreshesOnlyVisibleCardsUntilOthersAreShown()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        var timeProvider = (TestTimeProvider)Services.GetRequiredService<BrowserTimeProvider>();
        timeProvider.UtcNow = s_testTime.AddMinutes(2);

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await AddCountersAsync(value: 10, s_testTime.AddMinutes(1));

        var resource = Assert.Single(telemetryRepository.GetResources());
        var cut = Render<MeterOverview>(builder =>
        {
            builder.Add(c => c.ResourceKey, resource.ResourceKey);
            builder.Add(c => c.Instruments, telemetryRepository.GetInstrumentSummaries(resource.ResourceKey));
            builder.Add(c => c.Duration, TimeSpan.FromMinutes(5));
            builder.Add(c => c.ShowMeterName, false);
            builder.Add(c => c.SortBy, MetricSortBy.Instrument);
            builder.Add(c => c.GetInstrumentUrl, instrument => $"/metrics/{instrument.Name}");
        });
        cut.WaitForAssertion(() => Assert.Equal(["first.count: 10", "second.count: 10"], GetCardValues()));

        // Only the first card is on screen, so the periodic refresh leaves the second card's value as it was.
        var cardIds = cut.FindAll(".meter-card").Select(card => card.GetAttribute("data-instrument-id")!).ToList();
        await cut.InvokeAsync(() => cut.Instance.OnVisibleCardsChanged([cardIds[0]]));
        await AddCountersAsync(value: 500, s_testTime.AddMinutes(1.5));
        cut.WaitForAssertion(() => Assert.Equal(["first.count: 500", "second.count: 10"], GetCardValues()), TimeSpan.FromSeconds(10));

        // Scrolling the second card into view refreshes it right away.
        await cut.InvokeAsync(() => cut.Instance.OnVisibleCardsChanged([.. cardIds]));
        cut.WaitForAssertion(() => Assert.Equal(["first.count: 500", "second.count: 500"], GetCardValues()));

        Task AddCountersAsync(int value, DateTime time) => telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "first.count", startTime: time, value: value),
                            CreateSumMetric(metricName: "second.count", startTime: time, value: value)
                        }
                    }
                }
            }
        });

        List<string> GetCardValues() =>
            [.. cut.FindAll(".meter-card").Select(card => $"{card.QuerySelector(".meter-card-name")!.TextContent}: {card.QuerySelector(".meter-card-value-number")?.TextContent}")];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialLoad_TotalQuery_SetsCounterRunningTotal(bool total)
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.MetricsUrl(
            resource: "TestApp",
            meter: "test-meter",
            instrument: "test-instrument",
            duration: 5,
            view: MetricViewKind.Graph.ToString(),
            total: total));

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // The URL keeps the choice, so a link or back navigation shows the same view of the counter.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(total, cut.Instance.PageViewModel.ShowRunningTotal);
            Assert.Equal(total, cut.FindComponent<ChartContainer>().Instance.ShowRunningTotal);
        });
    }

    [Fact]
    public async Task ShowAllInstruments_ListsEveryMeterAndUpdatesUrl()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        await AddTwoMetersAsync();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 3);

        cut.Find(".metric-group-all").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.PageViewModel.ShowAll);
            Assert.Equal("All instruments", cut.Find(".meter-name-title").TextContent);
            Assert.Equal(["test-meter1: http.request.count", "test-meter1: memory.usage", "test-meter2: queue.length"], GetFilterResultsByMeter(cut));
            Assert.Equal("page", cut.Find(".metric-group-all").GetAttribute("aria-current"));
            Assert.Empty(cut.FindAll(".metric-group.selected:not(.metric-group-all)"));
            Assert.Contains("all=true", navigationManager.Uri);
        });
    }

    [Fact]
    public async Task SingleResource_OffersGroupingAndSortingWithoutResourceOptions()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        await AddTwoMetersAsync();

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 3);

        var menus = cut.FindComponents<AspireMenuButton>().Where(m => m.Instance.ButtonClass?.Contains("metrics-organize-button", StringComparison.Ordinal) == true).ToList();
        Assert.Collection(menus,
            group => Assert.Equal(["None", "Meter", "Instrument"], group.Instance.ItemsProvider().Where(i => !i.IsHeader).Select(i => i.Text)),
            sort => Assert.Equal(["Name", "Value (high to low)", "Value (low to high)"], sort.Instance.ItemsProvider().Where(i => !i.IsHeader).Select(i => i.Text)));

        // Instruments are grouped by meter and sorted by name by default.
        Assert.Equal("Meter", menus[0].Instance.ItemsProvider().Single(i => i.Checked).Text);
        Assert.Equal("Name", menus[1].Instance.ItemsProvider().Single(i => i.Checked).Text);

        cut.Find(".metric-group-all").Click();
        cut.WaitForAssertion(() => Assert.Equal(["test-meter1", "test-meter2"], cut.FindAll(".metrics-section-title").Select(t => t.TextContent)));
    }

    [Fact]
    public async Task ShowAllInstruments_GroupByNone_ListsInstrumentsInSortOrderWithoutSections()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics = { CreateSumMetric(metricName: "zeta.count", startTime: s_testTime.AddMinutes(1)) }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics = { CreateSumMetric(metricName: "alpha.count", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 2);

        var groupMenu = cut.FindComponents<AspireMenuButton>().First(m => m.Instance.ButtonClass?.Contains("metrics-organize-button", StringComparison.Ordinal) == true);
        cut.Find(".metric-group-all").Click();
        await cut.InvokeAsync(() => groupMenu.Instance.ItemsProvider().Single(i => i.Text == "None").OnClick!());

        // One list across meters, sorted by instrument name, with each card naming its meter.
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".metrics-section-title"));
            Assert.Equal(["alpha.count", "zeta.count"], GetCardNames(cut));
            Assert.Equal(["test-meter2", "test-meter1"], cut.FindAll(".meter-card-meter").Select(m => m.TextContent));
        });
    }

    [Fact]
    public async Task ShowAllInstruments_RendersAtMostTheLimitAndAsksToFilter()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var scopeMetrics = new ScopeMetrics { Scope = CreateScope(name: "test-meter1") };
        for (var i = 0; i < Metrics.MaxRenderedInstruments + 5; i++)
        {
            scopeMetrics.Metrics.Add(CreateSumMetric(metricName: $"instrument.{i:D3}", startTime: s_testTime.AddMinutes(1)));
        }
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics { Resource = CreateResource(name: "TestApp"), ScopeMetrics = { scopeMetrics } }
        });

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == Metrics.MaxRenderedInstruments + 5);

        cut.Find(".metric-group-all").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(Metrics.MaxRenderedInstruments, cut.FindAll(".meter-card-name").Count);
            Assert.Equal("Showing the first 200 of 205 instruments. Filter to narrow the list.", cut.Find(".metrics-limit-notice").TextContent.Trim());
        });
    }

    [Fact]
    public async Task InstrumentChart_Back_ReturnsToGroupItWasOpenedFrom()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);
        Services.GetRequiredService<MetricPinStore>().SetPinned(new OtlpInstrumentKey("test-meter2", "queue.length"), pinned: true);
        await AddTwoMetersAsync();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: "TestApp", duration: 5, pinned: true));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        cut.WaitForAssertion(() => Assert.True(cut.Instance.PageViewModel.ShowPinned));

        // Open the pinned instrument's chart, as clicking its card does.
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter2", instrument: "queue.length", duration: 5));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("queue.length", cut.Instance.PageViewModel.SelectedInstrument?.Name);
            Assert.Equal("Back to Pinned", cut.Find(".metrics-chart-back").GetAttribute("aria-label"));
        });

        cut.Find(".metrics-chart-back").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Instance.PageViewModel.ShowPinned);
            Assert.Null(cut.Instance.PageViewModel.SelectedInstrument);
            Assert.Contains("pinned=true", navigationManager.Uri);
        });
    }

    [Fact]
    public async Task MeterCards_InstrumentsWithoutData_AreLast()
    {
        MetricsSetupHelpers.SetupMetricsPage(this);

        // The page's clock is fixed in tests: data at that time is inside the cards' time range,
        // while data from 1970 is far outside it.
        var now = new DateTime(2025, 12, 20, 23, 59, 50, DateTimeKind.Utc);
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "a-without-data", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "b-with-data", startTime: now)
                        }
                    }
                }
            }
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", duration: 5));

        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() => Assert.Equal(["b-with-data", "a-without-data"], GetCardNames(cut)));
    }

    private async Task AddTwoMetersAsync()
    {
        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "http.request.count", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "memory.usage", startTime: s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics = { CreateSumMetric(metricName: "queue.length", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });
    }

    // Each card of the grouped results as "<meter>: <instrument>".
    private static List<string> GetFilterResultsByMeter(IRenderedComponent<Metrics> cut) =>
        [.. cut.FindAll(".metrics-filter-results-group").SelectMany(g => g.QuerySelectorAll(".meter-card-name")
            .Select(n => $"{g.QuerySelector(".metrics-section-title")!.TextContent}: {n.TextContent}"))];

    // Each group as "<name> <instrument count>".
    private static List<string> GetMetricGroups(IRenderedComponent<Metrics> cut) =>
        [.. cut.FindAll(".metric-group:not(.metric-group-all)").Select(g => $"{g.QuerySelector(".metric-group-name")!.TextContent} {g.QuerySelector(".metric-group-count")!.TextContent}")];

    private static List<string> GetCardNames(IRenderedComponent<Metrics> cut) =>
        [.. cut.FindAll(".meter-card-name").Select(n => n.TextContent)];

    private async Task ChangeResourceAndAssertInstrument(string app1InstrumentName, string app2InstrumentName, string? expectedMeterNameAfterChange, string? expectedInstrumentNameAfterChange)
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: app1InstrumentName, duration: 720, view: MetricViewKind.Table.ToString()));

        var telemetryRepository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await telemetryRepository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: app1InstrumentName, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: app2InstrumentName, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act 1
        var cut = Render<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        var viewModel = cut.Instance.PageViewModel;

        // Assert 1
        Assert.Equal("test-meter", viewModel.SelectedMeter);
        Assert.Equal(app1InstrumentName, viewModel.SelectedInstrument!.Name);

        // Act 2
        cut.Render(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp2");
        });

        cut.WaitForAssertion(() => Assert.Equal("TestApp2", cut.Instance.PageViewModel.SelectedResource.Name));
        await cut.InvokeAsync(cut.Instance.HandleSelectedResourceChangedAsync);
        viewModel = cut.Instance.PageViewModel;

        Assert.Equal("TestApp2", viewModel.SelectedResource.Name);
        Assert.Equal(expectedInstrumentNameAfterChange, viewModel.SelectedInstrument?.Name);
        Assert.Equal(expectedMeterNameAfterChange, viewModel.SelectedMeter);

        Assert.Equal(MetricViewKind.Table, viewModel.SelectedViewKind);
        Assert.Equal(TimeSpan.FromMinutes(720), viewModel.SelectedDuration.Id);
    }

    private sealed class SignalingFakeTimeProvider : FakeTimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }
}
