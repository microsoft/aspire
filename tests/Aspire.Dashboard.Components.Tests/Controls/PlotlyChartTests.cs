// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.HistogramTestHelpers;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class PlotlyChartTests : DashboardTestContext
{
    private static string GetContainerHtml(string divId) => $"""<div id="{divId}" class="plotly-chart-container"></div>""";

    [Fact]
    public async Task Render_HistogramExemplars_PreservesBrowserLocalPositionsAndTooltips()
    {
        var timeProvider = new TestTimeProvider { ConfiguredTimeFormat = TimeFormat.TwentyFourHour };
        FluentUISetupHelpers.AddCommonDashboardServices(this, browserTimeProvider: timeProvider);
        MetricsSetupHelpers.SetupPlotlyChart(this);
        var context = new OtlpContext { Options = new TelemetryLimitOptions(), Logger = NullLogger.Instance };
        var resource = new OtlpResource("resource", instanceId: null, uninstrumentedPeer: false, context);
        var summary = new OtlpInstrumentSummary
        {
            Name = "histogram",
            Unit = "ms",
            Description = string.Empty,
            Parent = new OtlpScope("meter", string.Empty, []),
            Type = OtlpInstrumentType.Histogram,
            AggregationTemporality = OtlpAggregationTemporality.Cumulative,
            ResourceView = new OtlpResourceView(resource, Array.Empty<KeyValuePair<string, string>>())
        };
        var sampleTime = timeProvider.GetUtcNow().AddSeconds(-2);
        var first = CreateExemplar(sampleTime.UtcDateTime, 1);
        first.TimeUnixNano += 10;
        var second = first.Clone();
        second.TimeUnixNano += 10;
        var point = CreatePoint(sampleTime.AddSeconds(-1).UtcDateTime, sampleTime.AddSeconds(1).UtcDateTime, [2, 0], [100]);
        point.Exemplars.AddRange([first, second]);
        var dimension = new DimensionScope(capacity: 100, []);
        dimension.AddHistogramValue(point, OtlpAggregationTemporality.Cumulative, context);
        var model = new InstrumentViewModel();
        await model.UpdateDataAsync(summary, [dimension], hasOverflow: false);

        var cut = Render<PlotlyChart>(builder =>
        {
            builder.Add(p => p.InstrumentViewModel, model);
            builder.Add(p => p.Duration, TimeSpan.FromSeconds(5));
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() =>
        {
            var invocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "initializeChart");
            var trace = Assert.IsType<PlotlyTrace>(invocation.Arguments[2]);
            var localSampleTime = timeProvider.ToLocalDateTimeOffset(sampleTime);
            Assert.Equal([localSampleTime, localSampleTime], trace.X);
            Assert.All(trace.X, time => Assert.Equal(TimeSpan.FromHours(1), time.Offset));
            Assert.Equal([1d, 1d], trace.Y);
            Assert.Equal(2, trace.TraceData.Count);
            Assert.All(trace.Tooltips, tooltip => Assert.Equal(
                $"""
                <b>Trace: {OtlpHelpers.ToShortenedId(first.TraceId.ToHexString())}</b><br />
                Value: 1 ms<br />
                Time: 0:59:57
                """,
                tooltip, ignoreLineEndingDifferences: true));
        });
    }

    [Fact]
    public void Render_NoInstrument_NoPlotlyInvocations()
    {
        // Arrange
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        MetricsSetupHelpers.SetupPlotlyChart(this);

        var model = new InstrumentViewModel();

        // Act
        var cut = Render<PlotlyChart>(builder =>
        {
            builder.Add(p => p.InstrumentViewModel, model);
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        cut.MarkupMatches(GetContainerHtml(cut.Instance.ChartDivId));

        Assert.Collection(JSInterop.Invocations,
            i =>
            {
                Assert.Equal("import", i.Identifier);
                Assert.Equal("/js/app-metrics.js", i.Arguments[0]);
            });
    }

    [Theory]
    [InlineData(TimeFormat.System, "12:59:57 AM", "%-I:%M:%S %p")]
    [InlineData(TimeFormat.TwelveHour, "12:59:57 AM", "%-I:%M:%S %p")]
    [InlineData(TimeFormat.TwentyFourHour, "0:59:57", "%H:%M:%S")]
    public async Task Render_HasInstrument_InitializeChartInvocation(TimeFormat timeFormat, string expectedTooltipTime, string expectedPlotlyTimeFormat)
    {
        // Arrange
        var timeProvider = new TestTimeProvider { ConfiguredTimeFormat = timeFormat };
        FluentUISetupHelpers.AddCommonDashboardServices(this, browserTimeProvider: timeProvider);
        MetricsSetupHelpers.SetupPlotlyChart(this);

        var options = new TelemetryLimitOptions();
        var logger = NullLogger.Instance;
        var context = new OtlpContext { Options = options, Logger = logger };
        var resource = new OtlpResource("resource", instanceId: null, uninstrumentedPeer: false, context);
        var instrumentSummary = new OtlpInstrumentSummary
        {
            Name = "Name-<b>Bold</b>",
            Unit = "Unit-<b>Bold</b>",
            Description = "Description-<b>Bold</b>",
            Parent = new OtlpScope("Parent-Name-<b>Bold</b>", string.Empty, []),
            Type = OtlpInstrumentType.Sum,
            AggregationTemporality = OtlpAggregationTemporality.Cumulative,
            ResourceView = new OtlpResourceView(resource, Array.Empty<KeyValuePair<string, string>>())
        };

        var model = new InstrumentViewModel();
        var dimension = new DimensionScope(capacity: 100, []);
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 1,
            StartTimeUnixNano = 0,
            TimeUnixNano = long.MaxValue
        }, context);

        await model.UpdateDataAsync(instrumentSummary, [dimension], hasOverflow: false);

        // Act
        var cut = Render<PlotlyChart>(builder =>
        {
            builder.Add(p => p.InstrumentViewModel, model);
            builder.Add(p => p.Duration, TimeSpan.FromSeconds(1));
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        cut.MarkupMatches(GetContainerHtml(cut.Instance.ChartDivId));

        Assert.Collection(JSInterop.Invocations,
            i =>
            {
                Assert.Equal("import", i.Identifier);
                Assert.Equal("/js/app-metrics.js", i.Arguments[0]);
            },
            i =>
            {
                Assert.Equal("initializeChart", i.Identifier);
                Assert.Equal(cut.Instance.ChartDivId, i.Arguments[0]);
                Assert.Collection((IEnumerable<PlotlyTrace>)i.Arguments[1]!, trace =>
                {
                    Assert.Equal("Unit-&lt;b&gt;Bold&lt;/b&gt;", trace.Name);
                    Assert.Equal($"<b>Name-&lt;b&gt;Bold&lt;/b&gt;</b><br />Unit-&lt;b&gt;Bold&lt;/b&gt;: 1<br />Time: {expectedTooltipTime}", trace.Tooltips[0], ignoreWhiteSpaceDifferences: true);
                });
                Assert.Equal(expectedPlotlyTimeFormat, Assert.IsType<PlotlyUserLocale>(i.Arguments[5]).Time);
            });
    }

    [Fact]
    public async Task UpdateDataAsync_SubscriptionRemovedDuringUpdate_CompletesSuccessfully()
    {
        var model = new InstrumentViewModel();
        var firstSubscriptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFirstSubscription = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubscriptionCalled = false;

        async Task FirstSubscription()
        {
            firstSubscriptionStarted.SetResult();
            await continueFirstSubscription.Task;
        }

        Task SecondSubscription()
        {
            secondSubscriptionCalled = true;
            return Task.CompletedTask;
        }

        model.AddDataUpdateSubscription(FirstSubscription);
        model.AddDataUpdateSubscription(SecondSubscription);

        var updateTask = model.UpdateDataAsync(null!, [], hasOverflow: false);
        await firstSubscriptionStarted.Task;

        model.RemoveDataUpdateSubscription(SecondSubscription);
        continueFirstSubscription.SetResult();

        await updateTask;

        Assert.True(secondSubscriptionCalled);
    }
}
