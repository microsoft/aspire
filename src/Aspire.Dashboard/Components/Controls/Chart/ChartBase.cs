// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Web;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Chart;

public abstract class ChartBase : ComponentBase, IAsyncDisposable
{
    private const int GraphPointCount = 30;

    private readonly CancellationTokenSource _cts = new();
    protected CancellationToken CancellationToken { get; private set; }

    private TimeSpan _tickDuration;
    private DateTimeOffset _lastUpdateTime;
    private DateTimeOffset _currentDataStartTime;
    private List<KeyValuePair<string, string>[]>? _renderedDimensionAttributes;
    private OtlpInstrumentKey? _renderedInstrument;
    private string? _renderedTheme;
    private bool _renderedShowCount;

    [Inject]
    public required IStringLocalizer<ControlsStrings> Loc { get; init; }

    [Inject]
    public required IInstrumentUnitResolver InstrumentUnitResolver { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Parameter, EditorRequired]
    public required InstrumentViewModel InstrumentViewModel { get; set; }

    [Parameter, EditorRequired]
    public required TimeSpan Duration { get; set; }

    [Parameter]
    public required List<OtlpResource> Resources { get; set; }

    // Stores a cache of the last set of spans returned as exemplars.
    // This dictionary is replaced each time the chart is updated.
    private Dictionary<SpanKey, OtlpSpan> _currentCache = new Dictionary<SpanKey, OtlpSpan>();
    private Dictionary<SpanKey, OtlpSpan> _newCache = new Dictionary<SpanKey, OtlpSpan>();

    protected override void OnInitialized()
    {
        // Copy the token so there is no chance it is accessed on CTS after it is disposed.
        CancellationToken = _cts.Token;
        _currentDataStartTime = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : GetCurrentDataTime();
        InstrumentViewModel.DataUpdateSubscriptions.Add(OnInstrumentDataUpdate);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (CancellationToken.IsCancellationRequested ||
            InstrumentViewModel.Instrument is null ||
            InstrumentViewModel.MatchedDimensions is null ||
            !ReadyForData())
        {
            return;
        }

        var inProgressDataTime = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : GetCurrentDataTime();

        // Only advance the time window when not paused. When paused, keep the chart's
        // time axis stable so filter changes don't cause the x-axis to jump.
        if (pausedAt is null)
        {
            while (_currentDataStartTime.Add(_tickDuration) < inProgressDataTime)
            {
                _currentDataStartTime = _currentDataStartTime.Add(_tickDuration);
            }
        }

        var dimensionAttributes = InstrumentViewModel.MatchedDimensions.Select(d => d.Attributes).ToList();
        if (_renderedInstrument is null || _renderedInstrument != InstrumentViewModel.Instrument.GetKey() ||
            _renderedDimensionAttributes is null || !_renderedDimensionAttributes.SequenceEqual(dimensionAttributes) ||
            _renderedTheme != InstrumentViewModel.Theme ||
            _renderedShowCount != InstrumentViewModel.ShowCount)
        {
            // Dimensions (or entire chart) has changed. Re-render the entire chart.
            _renderedInstrument = InstrumentViewModel.Instrument.GetKey();
            _renderedDimensionAttributes = dimensionAttributes;
            _renderedTheme = InstrumentViewModel.Theme;
            _renderedShowCount = InstrumentViewModel.ShowCount;
            await UpdateChartAsync(tickUpdate: false, inProgressDataTime).ConfigureAwait(false);
        }
        else if (_lastUpdateTime.Add(TimeSpan.FromSeconds(0.2)) < TimeProvider.GetUtcNow())
        {
            // Throttle how often the chart is updated.
            _lastUpdateTime = TimeProvider.GetUtcNow();
            await UpdateChartAsync(tickUpdate: true, inProgressDataTime).ConfigureAwait(false);
        }
    }

    protected override void OnParametersSet()
    {
        _tickDuration = Duration / GraphPointCount;
    }

    private Task OnInstrumentDataUpdate()
    {
        return InvokeAsync(StateHasChanged);
    }

    private (List<ChartTrace> Y, List<DateTimeOffset> X, List<ChartExemplar> Exemplars) CalculateHistogramValues(List<DimensionScope> dimensions, int pointCount, bool tickUpdate, DateTimeOffset inProgressDataTime, string yLabel)
    {
        var pointDuration = Duration / pointCount;
        var traces = new Dictionary<int, ChartTrace>
        {
            [50] = new() { Name = $"P50 {yLabel}", Percentile = 50 },
            [90] = new() { Name = $"P90 {yLabel}", Percentile = 90 },
            [99] = new() { Name = $"P99 {yLabel}", Percentile = 99 }
        };
        var xValues = new List<DateTimeOffset>();
        var exemplars = new List<ChartExemplar>();
        var startDate = _currentDataStartTime;
        DateTimeOffset? firstPointEndTime = null;
        DateTimeOffset? lastPointStartTime = null;

        // Generate the points in reverse order so that the chart is drawn from right to left.
        // Add a couple of extra points to the end so that the chart is drawn all the way to the right edge.
        for (var pointIndex = 0; pointIndex < (pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startDate, pointDuration);
            var end = CalcOffset(pointIndex - 1, startDate, pointDuration);
            firstPointEndTime ??= end;
            lastPointStartTime = start;

            xValues.Add(TimeProvider.ToLocalDateTimeOffset(end));

            if (!TryCalculateHistogramPoints(dimensions, start, end, traces, exemplars))
            {
                foreach (var trace in traces)
                {
                    trace.Value.Values.Add(null);
                }
            }
        }

        foreach (var item in traces)
        {
            item.Value.Values.Reverse();
        }
        xValues.Reverse();

        if (tickUpdate && TryCalculateHistogramPoints(dimensions, firstPointEndTime!.Value, inProgressDataTime, traces, exemplars))
        {
            xValues.Add(TimeProvider.ToLocalDateTimeOffset(inProgressDataTime));
        }

        ChartTrace? previousValues = null;
        foreach (var trace in traces.OrderBy(kvp => kvp.Key))
        {
            var currentTrace = trace.Value;

            for (var i = 0; i < currentTrace.Values.Count; i++)
            {
                double? diffValue = (previousValues != null)
                    ? currentTrace.Values[i] - previousValues.Values[i] ?? 0
                    : currentTrace.Values[i];

                if (diffValue > 0)
                {
                    currentTrace.Tooltips.Add(FormatTooltip(currentTrace.Name, currentTrace.Values[i].GetValueOrDefault(), xValues[i]));
                }
                else
                {
                    currentTrace.Tooltips.Add(null);
                }

                currentTrace.DiffValues.Add(diffValue);
            }

            previousValues = currentTrace;
        }

        exemplars = exemplars.Where(p => p.Start <= startDate && p.Start >= lastPointStartTime!.Value).OrderBy(p => p.Start).ToList();

        return (traces.Select(kvp => kvp.Value).ToList(), xValues, exemplars);
    }

    private string FormatTooltip(string name, double yValue, DateTimeOffset xValue)
    {
        return $"<b>{HttpUtility.HtmlEncode(InstrumentViewModel.Instrument?.Name)}</b><br />{HttpUtility.HtmlEncode(name)}: {FormatHelpers.FormatNumberWithOptionalDecimalPlaces(yValue, maxDecimalPlaces: 6, CultureInfo.CurrentCulture)}<br />Time: {FormatHelpers.FormatTime(TimeProvider, TimeProvider.ToLocal(xValue))}";
    }

    private static HistogramValue GetHistogramValue(MetricValueBase metric)
    {
        if (metric is HistogramValue histogramValue)
        {
            return histogramValue;
        }

        throw new InvalidOperationException("Unexpected metric type: " + metric.GetType());
    }

    internal bool TryCalculateHistogramPoints(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, Dictionary<int, ChartTrace> traces, List<ChartExemplar> exemplars)
    {
        var hasValue = false;

        ulong[]? currentBucketCounts = null;
        double[]? explicitBounds = null;

        start = start.Subtract(TimeSpan.FromSeconds(1));
        end = end.Add(TimeSpan.FromSeconds(1));

        foreach (var dimension in dimensions)
        {
            var dimensionValues = dimension.Values;
            for (var i = dimensionValues.Count - 1; i >= 0; i--)
            {
                var metric = dimensionValues[i];
                if (metric.Start >= start && metric.Start <= end)
                {
                    var histogramValue = GetHistogramValue(metric);

                    AddExemplars(exemplars, metric);

                    // Only use the first recorded entry if it is the beginning of data.
                    // We can verify the first entry is the beginning of data by checking if the number of buckets equals the total count.
                    if (i == 0 && CountBuckets(histogramValue) != histogramValue.Count)
                    {
                        continue;
                    }

                    explicitBounds ??= histogramValue.ExplicitBounds;

                    var previousHistogramValues = i > 0 ? GetHistogramValue(dimensionValues[i - 1]).Values : null;

                    if (currentBucketCounts is null)
                    {
                        currentBucketCounts = new ulong[histogramValue.Values.Length];
                    }
                    else if (currentBucketCounts.Length != histogramValue.Values.Length)
                    {
                        throw new InvalidOperationException("Histogram values changed size");
                    }

                    for (var valuesIndex = 0; valuesIndex < histogramValue.Values.Length; valuesIndex++)
                    {
                        var newValue = histogramValue.Values[valuesIndex];

                        if (previousHistogramValues != null)
                        {
                            // Histogram values are cumulative, so subtract the previous value to get the diff.
                            newValue -= previousHistogramValues[valuesIndex];
                        }

                        currentBucketCounts[valuesIndex] += newValue;
                    }

                    hasValue = true;
                }
            }
        }
        if (hasValue)
        {
            foreach (var percentileValues in traces)
            {
                var percentileValue = CalculatePercentile(percentileValues.Key, currentBucketCounts!, explicitBounds!);
                percentileValues.Value.Values.Add(percentileValue);
            }
        }
        return hasValue;
    }

    private void AddExemplars(List<ChartExemplar> exemplars, MetricValueBase metric)
    {
        if (metric.HasExemplars)
        {
            foreach (var exemplar in metric.Exemplars)
            {
                // TODO: Exemplars are duplicated on metrics in some scenarios.
                // This is a quick fix to ensure a distinct collection of metrics are displayed in the UI.
                // Investigation is needed into why there are duplicates.
                var exists = false;
                foreach (var existingExemplar in exemplars)
                {
                    if (exemplar.Start == existingExemplar.Start &&
                        exemplar.Value == existingExemplar.Value &&
                        exemplar.SpanId == existingExemplar.SpanId &&
                        exemplar.TraceId == existingExemplar.TraceId)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                {
                    continue;
                }

                // Try to find span the the local cache first.
                // This is done to avoid scanning a potentially large trace collection in repository.
                var key = new SpanKey(exemplar.TraceId, exemplar.SpanId);
                if (!_currentCache.TryGetValue(key, out var span))
                {
                    span = TelemetryRepository.GetSpan(exemplar.TraceId, exemplar.SpanId);
                }
                if (span != null)
                {
                    _newCache[key] = span;
                }

                var exemplarStart = TimeProvider.ToLocalDateTimeOffset(exemplar.Start);
                exemplars.Add(new ChartExemplar
                {
                    Start = exemplarStart,
                    Value = exemplar.Value,
                    TraceId = exemplar.TraceId,
                    SpanId = exemplar.SpanId,
                    Span = span
                });
            }
        }
    }

    private static ulong CountBuckets(HistogramValue histogramValue)
    {
        ulong value = 0ul;
        for (var i = 0; i < histogramValue.Values.Length; i++)
        {
            value += histogramValue.Values[i];
        }
        return value;
    }

    internal static double? CalculatePercentile(int percentile, ulong[] counts, double[] explicitBounds)
    {
        if (percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be between 0 and 100.");
        }

        var totalCount = 0ul;
        foreach (var count in counts)
        {
            totalCount += count;
        }

        var targetCount = (percentile / 100.0) * totalCount;
        var accumulatedCount = 0ul;

        for (var i = 0; i < explicitBounds.Length; i++)
        {
            accumulatedCount += counts[i];

            if (accumulatedCount >= targetCount)
            {
                return explicitBounds[i];
            }
        }

        // If the percentile is larger than any bucket value, return the last value
        return explicitBounds[explicitBounds.Length - 1];
    }

    private (List<ChartTrace> Y, List<DateTimeOffset> X, List<ChartExemplar> Exemplars) CalculateChartValues(List<DimensionScope> dimensions, int pointCount, bool tickUpdate, DateTimeOffset inProgressDataTime, string yLabel)
    {
        var pointDuration = Duration / pointCount;
        var yValues = new List<double?>();
        var xValues = new List<DateTimeOffset>();
        var exemplars = new List<ChartExemplar>();
        var startDate = _currentDataStartTime;
        DateTimeOffset? firstPointEndTime = null;

        // Generate the points in reverse order so that the chart is drawn from right to left.
        // Add a couple of extra points to the end so that the chart is drawn all the way to the right edge.
        for (var pointIndex = 0; pointIndex < (pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startDate, pointDuration);
            var end = CalcOffset(pointIndex - 1, startDate, pointDuration);
            firstPointEndTime ??= end;

            xValues.Add(TimeProvider.ToLocalDateTimeOffset(end));

            if (TryCalculatePoint(dimensions, start, end, exemplars, out var tickPointValue))
            {
                yValues.Add(tickPointValue);
            }
            else
            {
                yValues.Add(null);
            }
        }

        yValues.Reverse();
        xValues.Reverse();

        if (tickUpdate && TryCalculatePoint(dimensions, firstPointEndTime!.Value, inProgressDataTime, exemplars, out var inProgressPointValue))
        {
            yValues.Add(inProgressPointValue);
            xValues.Add(TimeProvider.ToLocalDateTimeOffset(inProgressDataTime));
        }

        var trace = new ChartTrace
        {
            Name = HttpUtility.HtmlEncode(yLabel)
        };

        for (var i = 0; i < xValues.Count; i++)
        {
            trace.Values.AddRange(yValues);
            trace.DiffValues.AddRange(yValues);
            if (yValues[i] is not null)
            {
                trace.Tooltips.Add(FormatTooltip(yLabel, yValues[i]!.Value, xValues[i]));
            }
            else
            {
                trace.Tooltips.Add(null);
            }
        }

        return ([trace], xValues, exemplars);
    }

    private bool TryCalculatePoint(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, List<ChartExemplar> exemplars, out double pointValue)
    {
        var hasValue = false;
        pointValue = 0d;

        foreach (var dimension in dimensions)
        {
            var dimensionValues = dimension.Values;
            var dimensionValue = 0d;
            for (var i = dimensionValues.Count - 1; i >= 0; i--)
            {
                var metric = dimensionValues[i];
                if ((metric.Start <= end && metric.End >= start) || (metric.Start >= start && metric.End <= end))
                {
                    var value = metric switch
                    {
                        MetricValue<long> longMetric => longMetric.Value,
                        MetricValue<double> doubleMetric => doubleMetric.Value,
                        HistogramValue histogramValue => histogramValue.Count,
                        _ => 0// throw new InvalidOperationException("Unexpected metric type: " + metric.GetType())
                    };

                    dimensionValue = Math.Max(value, dimensionValue);
                    hasValue = true;
                }

                AddExemplars(exemplars, metric);
            }

            pointValue += dimensionValue;
        }

        // JS interop doesn't support serializing NaN values.
        if (double.IsNaN(pointValue))
        {
            pointValue = default;
            return false;
        }

        return hasValue;
    }

    private static DateTimeOffset CalcOffset(int pointIndex, DateTimeOffset now, TimeSpan pointDuration)
    {
        return now.Subtract(pointDuration * pointIndex);
    }

    private async Task UpdateChartAsync(bool tickUpdate, DateTimeOffset inProgressDataTime)
    {
        // Unit comes from the instrument and they're not localized.
        // The hardcoded "Count" label isn't localized for consistency.
        const string CountUnit = "Count";

        Debug.Assert(InstrumentViewModel.MatchedDimensions != null);
        Debug.Assert(InstrumentViewModel.Instrument != null);

        var unit = !InstrumentViewModel.ShowCount
            ? GetDisplayedUnit(InstrumentViewModel.Instrument)
            : CountUnit;

        List<ChartTrace> traces;
        List<DateTimeOffset> xValues;
        List<ChartExemplar> exemplars;
        if (InstrumentViewModel.Instrument?.Type != OtlpInstrumentType.Histogram || InstrumentViewModel.ShowCount)
        {
            (traces, xValues, exemplars) = CalculateChartValues(InstrumentViewModel.MatchedDimensions, GraphPointCount, tickUpdate, inProgressDataTime, unit);

            // TODO: Exemplars on non-histogram charts doesn't work well. Don't display for now.
            exemplars.Clear();
        }
        else
        {
            (traces, xValues, exemplars) = CalculateHistogramValues(InstrumentViewModel.MatchedDimensions, GraphPointCount, tickUpdate, inProgressDataTime, unit);
        }

        // Replace cache for next update.
        _currentCache = _newCache;
        _newCache = new Dictionary<SpanKey, OtlpSpan>();

        await OnChartUpdatedAsync(traces, xValues, exemplars, tickUpdate, inProgressDataTime, CancellationToken);
    }

    private DateTimeOffset GetCurrentDataTime()
    {
        return TimeProvider.GetUtcNow().Subtract(TimeSpan.FromSeconds(1)); // Compensate for delay in receiving metrics from services.
    }

    private string GetDisplayedUnit(OtlpInstrumentSummary instrument)
    {
        return InstrumentUnitResolver.ResolveDisplayedUnit(instrument, titleCase: true, pluralize: true);
    }

    protected abstract Task OnChartUpdatedAsync(List<ChartTrace> traces, List<DateTimeOffset> xValues, List<ChartExemplar> exemplars, bool tickUpdate, DateTimeOffset inProgressDataTime, CancellationToken cancellationToken);

    protected abstract bool ReadyForData();

    public ValueTask DisposeAsync() => DisposeAsync(disposing: true);

    protected virtual ValueTask DisposeAsync(bool disposing)
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
