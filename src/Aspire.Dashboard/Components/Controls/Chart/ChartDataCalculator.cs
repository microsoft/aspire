// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Components.Controls.Chart;

/// <summary>
/// Computes chart data (time-bucketed values and exemplars) from metric dimensions.
/// Produces raw traces and x-values without tooltips or span resolution — those are
/// added by the consuming Blazor component.
/// </summary>
internal sealed class ChartDataCalculator
{
    private readonly int _pointCount;
    private readonly TimeSpan _duration;

    public ChartDataCalculator(int pointCount, TimeSpan duration)
    {
        _pointCount = pointCount;
        _duration = duration;
    }

    public ChartData CalculateChartValues(List<DimensionScope> dimensions, DateTimeOffset startTime, Func<DateTimeOffset, DateTimeOffset> toLocal, string yLabel)
    {
        var pointDuration = _duration / _pointCount;
        var yValues = new List<double?>();
        var xValues = new List<DateTimeOffset>();
        var isHistogram = dimensions.Exists(static dimension => dimension.Values.Count > 0 && dimension.Values[0] is HistogramValue);
        var hasIncompatibleBounds = false;

        // Generate the points in reverse order so that the chart is drawn from right to left.
        // Add a couple of extra points to the end so that the chart is drawn all the way to the right edge.
        for (var pointIndex = 0; pointIndex < (_pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startTime, pointDuration);
            var end = CalcOffset(pointIndex - 1, startTime, pointDuration);

            xValues.Add(toLocal(end));

            if (TryCalculatePoint(dimensions, start, end, out var tickPointValue))
            {
                yValues.Add(tickPointValue);
            }
            else
            {
                yValues.Add(null);
            }

            // Compatibility is a property of the histogram data, not the selected visualization.
            // Count mode only intersects contributing bounds; it does not accumulate percentile buckets.
            if (isHistogram && !hasIncompatibleBounds &&
                TryAggregateHistogram(dimensions, start, end, collectBucketCounts: false, exemplars: null, toLocal, out _, out var bounds))
            {
                hasIncompatibleBounds = bounds!.Length == 0;
            }
        }

        yValues.Reverse();
        xValues.Reverse();

        var trace = new ChartTrace
        {
            Name = yLabel
        };
        trace.Values.AddRange(yValues);
        trace.DiffValues.AddRange(yValues);

        return new ChartData
        {
            Traces = [trace],
            XValues = xValues,
            // Exemplars on non-histogram charts don't work well and are cleared by the caller.
            Exemplars = [],
            HasIncompatibleHistogramBounds = hasIncompatibleBounds
        };
    }

    public ChartData CalculateHistogramValues(List<DimensionScope> dimensions, DateTimeOffset startTime, Func<DateTimeOffset, DateTimeOffset> toLocal, string yLabel)
    {
        var pointDuration = _duration / _pointCount;
        var traces = new Dictionary<int, ChartTrace>
        {
            [50] = new() { Name = $"P50 {yLabel}", Percentile = 50 },
            [90] = new() { Name = $"P90 {yLabel}", Percentile = 90 },
            [99] = new() { Name = $"P99 {yLabel}", Percentile = 99 }
        };
        var xValues = new List<DateTimeOffset>();
        var exemplars = new List<ChartExemplar>();
        var hasIncompatibleBounds = false;
        DateTimeOffset? lastPointStartTime = null;

        // Generate the points in reverse order so that the chart is drawn from right to left.
        // Add a couple of extra points to the end so that the chart is drawn all the way to the right edge.
        for (var pointIndex = 0; pointIndex < (_pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startTime, pointDuration);
            var end = CalcOffset(pointIndex - 1, startTime, pointDuration);
            lastPointStartTime = start;

            xValues.Add(toLocal(end));

            if (!TryCalculateHistogramPoints(dimensions, start, end, traces, exemplars, toLocal, out var incompatibleBounds))
            {
                foreach (var trace in traces)
                {
                    trace.Value.Values.Add(null);
                }
            }
            hasIncompatibleBounds |= incompatibleBounds;
        }

        foreach (var item in traces)
        {
            item.Value.Values.Reverse();
        }
        xValues.Reverse();

        ChartTrace? previousValues = null;
        foreach (var trace in traces.OrderBy(kvp => kvp.Key))
        {
            var currentTrace = trace.Value;

            // Stacked percentile bands must keep gaps when either percentile is unavailable.
            for (var i = 0; i < currentTrace.Values.Count; i++)
            {
                var diffValue = previousValues is not null
                    ? currentTrace.Values[i] - previousValues.Values[i]
                    : currentTrace.Values[i];

                currentTrace.DiffValues.Add(diffValue);
            }

            previousValues = currentTrace;
        }

        exemplars = exemplars.Where(p => p.Start <= startTime && p.Start >= lastPointStartTime!.Value).OrderBy(p => p.Start).ToList();

        return new ChartData
        {
            Traces = traces.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList(),
            XValues = xValues,
            Exemplars = exemplars,
            HasIncompatibleHistogramBounds = hasIncompatibleBounds
        };
    }

    internal static bool TryCalculatePoint(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, out double pointValue)
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
                // MetricValueBase.Start/End are DateTime (Kind=Utc from Unix timestamps).
                // Use explicit DateTimeOffset conversion to avoid silent local-time assumption
                // if a DateTime with Kind=Unspecified is ever stored.
                var metricStart = new DateTimeOffset(metric.Start, TimeSpan.Zero);
                var metricEnd = new DateTimeOffset(metric.End, TimeSpan.Zero);

                // Values are stored chronologically (oldest at index 0). We iterate newest-first,
                // so once a metric ends before our window starts, all remaining are older — stop.
                if (metricEnd < start)
                {
                    break;
                }

                if (metric is HistogramValue histogram)
                {
                    if (histogram.AggregationTemporality == OtlpAggregationTemporality.Delta)
                    {
                        if (metricStart >= start && metricStart < end)
                        {
                            dimensionValue += histogram.Count;
                            hasValue = true;
                        }
                        continue;
                    }

                    if (metricStart < end)
                    {
                        // Cumulative counts can decrease after a reset. Use the latest matching
                        // snapshot, not the maximum count from before the reset. An interval
                        // starting at the window end belongs to the following window.
                        dimensionValue = histogram.Count;
                        hasValue = true;
                        break;
                    }
                    continue;
                }

                if (metricStart <= end)
                {
                    var value = metric switch
                    {
                        MetricValue<long> longMetric => longMetric.Value,
                        MetricValue<double> doubleMetric => doubleMetric.Value,
                        _ => 0
                    };

                    dimensionValue = Math.Max(value, dimensionValue);
                    hasValue = true;
                }
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

    internal static bool TryCalculateHistogramPoints(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, Dictionary<int, ChartTrace> traces, List<ChartExemplar> exemplars, Func<DateTimeOffset, DateTimeOffset> toLocal, out bool incompatibleBounds)
    {
        var hasValue = TryAggregateHistogram(dimensions, start, end, collectBucketCounts: true, exemplars, toLocal,
            out var currentBucketCounts, out var explicitBounds);
        incompatibleBounds = hasValue && explicitBounds!.Length == 0;

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

    private static bool TryAggregateHistogram(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end,
        bool collectBucketCounts, List<ChartExemplar>? exemplars, Func<DateTimeOffset, DateTimeOffset> toLocal,
        out ulong[]? currentBucketCounts, out double[]? explicitBounds)
    {
        var hasValue = false;
        currentBucketCounts = null;
        explicitBounds = null;

        var cumulativeStart = start.Subtract(TimeSpan.FromSeconds(1));
        var cumulativeEnd = end.Add(TimeSpan.FromSeconds(1));

        foreach (var dimension in dimensions)
        {
            var dimensionValues = dimension.Values;
            for (var i = dimensionValues.Count - 1; i >= 0; i--)
            {
                var metric = dimensionValues[i];
                // MetricValueBase.Start is DateTime (Kind=Utc from Unix timestamps).
                // Use explicit DateTimeOffset conversion to avoid silent local-time assumption.
                var metricStart = new DateTimeOffset(metric.Start, TimeSpan.Zero);
                var histogramValue = GetHistogramValue(metric);
                var isDelta = histogramValue.AggregationTemporality == OtlpAggregationTemporality.Delta;
                // Retain the existing cumulative sampling tolerance. Delta intervals must belong
                // to exactly one half-open chart window, or their observations would be double-counted.
                if (isDelta
                    ? metricStart >= start && metricStart < end
                    : metricStart >= cumulativeStart && metricStart <= cumulativeEnd)
                {
                    if (exemplars is not null)
                    {
                        CollectExemplars(exemplars, metric, toLocal);
                    }

                    // Only use the first recorded entry if it is the beginning of data.
                    // We can verify the first entry is the beginning of data by checking if the number of buckets equals the total count.
                    if (i == 0 && CountBuckets(histogramValue) != histogramValue.Count)
                    {
                        continue;
                    }

                    var previous = !isDelta && i > 0 ? GetHistogramValue(dimensionValues[i - 1]) : null;
                    var previousHistogramValues = previous is not null && previous.AggregationId == histogramValue.AggregationId ? previous.Values : null;
                    var added = collectBucketCounts
                        ? HistogramBuckets.Add(ref currentBucketCounts, ref explicitBounds, histogramValue.Values,
                            histogramValue.ExplicitBounds, previousHistogramValues.AsSpan())
                        : HistogramBuckets.AddBounds(ref explicitBounds, histogramValue.Values,
                            histogramValue.ExplicitBounds, previousHistogramValues.AsSpan());
                    if (added)
                    {
                        hasValue = true;
                        if (!collectBucketCounts && explicitBounds!.Length == 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return hasValue;
    }

    internal static double? CalculatePercentile(int percentile, ulong[] counts, double[] explicitBounds)
    {
        if (percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be between 0 and 100.");
        }

        // counts has explicitBounds.Length + 1 entries. The last entry is the overflow
        // bucket (+Inf) for values exceeding the last explicit bound.
        var totalCount = 0ul;
        foreach (var count in counts)
        {
            totalCount += count;
        }

        if (totalCount == 0 || explicitBounds.Length == 0)
        {
            return null;
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

        // The percentile falls in the overflow (+Inf) bucket. There is no upper bound
        // for this bucket, so return the last explicit bound as the best available estimate.
        return explicitBounds[explicitBounds.Length - 1];
    }

    internal static DateTimeOffset CalcOffset(int pointIndex, DateTimeOffset now, TimeSpan pointDuration)
    {
        return now.Subtract(pointDuration * pointIndex);
    }

    private static void CollectExemplars(List<ChartExemplar> exemplars, MetricValueBase metric, Func<DateTimeOffset, DateTimeOffset> toLocal)
    {
        if (!metric.HasExemplars)
        {
            return;
        }

        foreach (var exemplar in metric.Exemplars)
        {
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

            var exemplarStart = toLocal(new DateTimeOffset(exemplar.Start, TimeSpan.Zero));
            exemplars.Add(new ChartExemplar
            {
                Start = exemplarStart,
                Value = exemplar.Value,
                TraceId = exemplar.TraceId,
                SpanId = exemplar.SpanId,
                Span = null
            });
        }
    }

    private static HistogramValue GetHistogramValue(MetricValueBase metric)
    {
        if (metric is HistogramValue histogramValue)
        {
            return histogramValue;
        }

        throw new InvalidOperationException("Unexpected metric type: " + metric.GetType());
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
}
