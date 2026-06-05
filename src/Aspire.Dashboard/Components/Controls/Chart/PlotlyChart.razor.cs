// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Web;
using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class PlotlyChart : ChartBase
{
    private static int s_nextChartId;

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> DialogsLoc { get; init; }

    public string ChartDivId { get; } = $"plotly-chart-container-{Interlocked.Increment(ref s_nextChartId)}";

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    private DotNetObjectReference<ChartInterop>? _chartInteropReference;
    private IJSObjectReference? _jsModule;

    private string FormatTooltip(string title, double yValue, DateTimeOffset xValue)
    {
        var formattedValue = FormatHelpers.FormatNumberWithOptionalDecimalPlaces(yValue, maxDecimalPlaces: 3, CultureInfo.CurrentCulture);
        if (InstrumentViewModel?.Instrument is { } instrument)
        {
            formattedValue += " " + InstrumentUnitResolver.ResolveDisplayedUnit(instrument, titleCase: false, pluralize: yValue != 1);
        }
        return $"""
                <b>{HttpUtility.HtmlEncode(title)}</b><br />
                {Loc[nameof(ControlsStrings.PlotlyChartValue)]}: {formattedValue}<br />
                {Loc[nameof(ControlsStrings.PlotlyChartTime)]}: {FormatHelpers.FormatTime(TimeProvider, TimeProvider.ToLocal(xValue))}
                """;
    }

    protected override async Task OnChartUpdatedAsync(List<ChartTrace> traces, List<DateTimeOffset> xValues, List<ChartExemplar> exemplars, bool tickUpdate, DateTimeOffset inProgressDataTime, CancellationToken cancellationToken)
    {
        Debug.Assert(_jsModule != null, "The module should be initialized before chart data is sent to control.");

        var traceDtos = traces.Select(t => new PlotlyTrace
        {
            Name = t.Name,
            Y = t.DiffValues,
            X = xValues,
            Tooltips = t.Tooltips,
            TraceData = new List<object?>()
        }).ToArray();

        var exemplarTraceDto = CalculateExemplarsTrace(xValues, exemplars);

        if (!tickUpdate)
        {
            // The chart mostly shows numbers but some localization is needed for displaying time ticks.
            var is24Hour = DateTimeFormatInfo.CurrentInfo.LongTimePattern.StartsWith("H", StringComparison.Ordinal);
            // Plotly uses d3-time-format https://d3js.org/d3-time-format
            var time = is24Hour ? "%H:%M:%S" : "%-I:%M:%S %p";
            var userLocale = new PlotlyUserLocale
            {
                Periods = [DateTimeFormatInfo.CurrentInfo.AMDesignator, DateTimeFormatInfo.CurrentInfo.PMDesignator],
                Time = time
            };

            _chartInteropReference?.Dispose();
            _chartInteropReference = DotNetObjectReference.Create(new ChartInterop(this));

            await _jsModule.InvokeVoidAsync(
                "initializeChart",
                ChartDivId,
                traceDtos,
                exemplarTraceDto,
                TimeProvider.ToLocal(inProgressDataTime),
                TimeProvider.ToLocal(inProgressDataTime - Duration).ToLocalTime(),
                userLocale,
                _chartInteropReference).ConfigureAwait(false);
        }
        else
        {
            await _jsModule.InvokeVoidAsync(
                "updateChart",
                ChartDivId,
                traceDtos,
                exemplarTraceDto,
                TimeProvider.ToLocal(inProgressDataTime),
                TimeProvider.ToLocal(inProgressDataTime - Duration)).ConfigureAwait(false);
        }
    }

    private PlotlyTrace CalculateExemplarsTrace(List<DateTimeOffset> xValues, List<ChartExemplar> exemplars)
    {
        // In local development there is no sampling of traces. There could be a very high number.
        // Too many points on the graph will impact browser performance, and is not useful anyway as they will
        // draw on top of each other and can't be used. Fix both of these problems by enforcing a maximum limit.
        //
        // Displaying up to a maximum number of exemplars per tick will display a continuous number of ticks across the graph.
        const int MaxExemplarsPerTick = 20;

        // Group exemplars into ticks based on xValues.
        var exemplarGroups = new Dictionary<ExemplarGroupKey, List<ChartExemplar>>();
        for (var i = 0; i <= xValues.Count; i++)
        {
            var start = i > 0 ? xValues[i - 1] : (DateTimeOffset?)null;
            var end = i < xValues.Count ? xValues[i] : (DateTimeOffset?)null;
            var g = new ExemplarGroupKey(start, end);

            var groupExemplars = exemplars.Where(e => (e.Start >= g.Start || g.Start == null) && (e.Start < g.End || g.End == null)).ToList();

            // When exemplars exceeds the limit then sample the exemplars to reduce data to the limit.
            if (groupExemplars.Count > MaxExemplarsPerTick)
            {
                var step = (double)groupExemplars.Count / MaxExemplarsPerTick;

                var sampledList = new List<ChartExemplar>(MaxExemplarsPerTick);
                for (var j = 0; j < MaxExemplarsPerTick; j++)
                {
                    // Calculate the index to take from the original list
                    var index = (int)Math.Floor(j * step);
                    sampledList.Add(groupExemplars[index]);
                }

                groupExemplars = sampledList;
            }

            exemplarGroups.Add(g, groupExemplars);
        }

        var exemplarTraceDto = new PlotlyTrace
        {
            Name = Loc[nameof(ControlsStrings.PlotlyChartExemplars)],
            Y = new List<double?>(),
            X = new List<DateTimeOffset>(),
            Tooltips = new List<string?>(),
            TraceData = new List<object?>()
        };

        foreach (var exemplar in exemplarGroups.SelectMany(g => g.Value))
        {
            string title;
            if (exemplar.Span != null)
            {
                title = SpanWaterfallViewModel.GetTitle(exemplar.Span, Resources);
            }
            else if (!string.IsNullOrEmpty(exemplar.TraceId))
            {
                // Exemplar has trace information but isn't matched to a span in the system.
                title = $"{Loc[nameof(ControlsStrings.PlotlyChartTrace)]}: {OtlpHelpers.ToShortenedId(exemplar.TraceId)}";
            }
            else
            {
                // Exemplar has no span information. Use generic title.
                title = Loc[nameof(ControlsStrings.PlotlyChartExemplar)];
            }

            var tooltip = FormatTooltip(title, exemplar.Value, exemplar.Start);

            exemplarTraceDto.X.Add(exemplar.Start);
            exemplarTraceDto.Y.Add(exemplar.Value);
            exemplarTraceDto.Tooltips.Add(tooltip);
            exemplarTraceDto.TraceData.Add(new { TraceId = exemplar.TraceId, SpanId = exemplar.SpanId });
        }

        return exemplarTraceDto;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/app-metrics.js");
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    // The first data is used to initialize the chart. The module needs to be ready first.
    protected override bool ReadyForData() => _jsModule != null;

    protected override async ValueTask DisposeAsync(bool disposing)
    {
        await base.DisposeAsync(disposing);

        if (disposing)
        {
            _chartInteropReference?.Dispose();
            await JSInteropHelpers.SafeDisposeAsync(_jsModule);
        }
    }

    /// <summary>
    /// Handle user clicking on a trace point in the browser.
    /// </summary>
    private sealed class ChartInterop
    {
        private readonly PlotlyChart _plotlyChart;

        public ChartInterop(PlotlyChart plotlyChart)
        {
            _plotlyChart = plotlyChart;
        }

        [JSInvokable]
        public async Task ViewSpan(string traceId, string spanId)
        {
            var available = await TraceLinkHelpers.WaitForSpanToBeAvailableAsync(
                traceId,
                spanId,
                _plotlyChart.TelemetryRepository.GetSpan,
                _plotlyChart.DialogService,
                _plotlyChart.InvokeAsync,
                _plotlyChart.DialogsLoc,
                _plotlyChart.CancellationToken).ConfigureAwait(false);

            if (available)
            {
                await _plotlyChart.InvokeAsync(() =>
                {
                    _plotlyChart.NavigationManager.NavigateTo(DashboardUrls.TraceDetailUrl(traceId, spanId));
                });
            }
        }
    }

    private readonly record struct ExemplarGroupKey(DateTimeOffset? Start, DateTimeOffset? End);
}
