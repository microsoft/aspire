// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class PlotlyChartTests : DashboardTestContext
{
    private static string GetContainerHtml(string divId) => $"""<div id="{divId}" class="plotly-chart-container"></div>""";

    [Fact]
    public void Render_NoInstrument_NoPlotlyInvocations()
    {
        // Arrange
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        MetricsSetupHelpers.SetupPlotlyChart(this);

        var model = new InstrumentViewModel();

        // Act
        var cut = RenderComponent<PlotlyChart>(builder =>
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
        var instrument = new OtlpInstrument
        {
            Summary = new OtlpInstrumentSummary
            {
                Name = "Name-<b>Bold</b>",
                Unit = "Unit-<b>Bold</b>",
                Description = "Description-<b>Bold</b>",
                Parent = new OtlpScope("Parent-Name-<b>Bold</b>", string.Empty, []),
                Type = OtlpInstrumentType.Sum,
                AggregationTemporality = OtlpAggregationTemporality.Cumulative
            },
            Context = context
        };

        var model = new InstrumentViewModel();
        var dimension = new DimensionScope(capacity: 100, []);
        dimension.AddPointValue(new NumberDataPoint
        {
            AsInt = 1,
            StartTimeUnixNano = 0,
            TimeUnixNano = long.MaxValue
        }, context);

        await model.UpdateDataAsync(instrument.Summary, [dimension]);

        // Act
        var cut = RenderComponent<PlotlyChart>(builder =>
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
}
