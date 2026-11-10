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
                TryAggregateHistogram(dimensions, start, end, collectBucketCounts: false, exemplars: null, out _, out var bounds))
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

            if (!TryCalculateHistogramPoints(dimensions, start, end, traces, exemplars, out var incompatibleBounds))
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

    /// <summary>
    /// Gets whether the instrument is a counter: a sum that only increases. Its values are a running total,
    /// so the change per interval is usually more telling than the total itself.
    /// </summary>
    internal static bool IsMonotonicCounter(OtlpInstrumentSummary instrument)
        => instrument.Type == OtlpInstrumentType.Sum && instrument.IsMonotonic;

    /// <summary>
    /// Calculates a counter's increase in each interval of <see cref="CalculateChartValues"/>.
    /// </summary>
    public List<double?> CalculateCounterIncreaseValues(List<DimensionScope> dimensions, DateTimeOffset startTime, OtlpAggregationTemporality temporality)
    {
        if (temporality == OtlpAggregationTemporality.Delta)
        {
            return CalculateDeltaIncreases(dimensions, startTime);
        }

        // A cumulative counter has a running total per dimension (attribute set). The increases are calculated per
        // dimension and then added up, rather than calculated from the sum of the totals. A dimension can be missing
        // from an interval, for example when it's exported less often, and a sum of A=10,000 and B=5,000 that drops
        // to 10,000 without B would otherwise look like a counter reset followed by an increase of 10,000.
        var totals = new FiniteSum[_pointCount + 2];
        var hasTotal = new bool[_pointCount + 2];
        foreach (var dimension in dimensions)
        {
            var values = CalculateChartValues([dimension], startTime, static t => t, string.Empty).Traces[0].Values;
            var increases = CalculateCounterIncreases(values, temporality);
            for (var i = 0; i < increases.Count; i++)
            {
                if (increases[i] is { } increase)
                {
                    totals[i].Add(increase);
                    hasTotal[i] = true;
                }
            }
        }

        return [.. totals.Select((total, i) => hasTotal[i] ? total.Value : (double?)null)];
    }

    private List<double?> CalculateDeltaIncreases(List<DimensionScope> dimensions, DateTimeOffset startTime)
    {
        var pointDuration = _duration / _pointCount;
        var values = new List<double?>(_pointCount + 2);

        // Same points as CalculateChartValues, generated in reverse order, so the results line up with its x-values.
        for (var pointIndex = 0; pointIndex < (_pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startTime, pointDuration);
            var end = CalcOffset(pointIndex - 1, startTime, pointDuration);
            values.Add(TryCalculateDeltaPoint(dimensions, start, end, out var value) ? value : null);
        }

        values.Reverse();
        return values;
    }

    /// <summary>
    /// Adds up the delta observations in an interval. Each one is a change, so unlike the level that
    /// <see cref="TryCalculatePoint"/> calculates, all of the observations in the interval count.
    /// </summary>
    internal static bool TryCalculateDeltaPoint(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, out double pointValue)
    {
        var hasValue = false;
        var sum = new FiniteSum();
        var startUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(start.UtcDateTime);
        var endUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(end.UtcDateTime);

        foreach (var dimension in dimensions)
        {
            var dimensionValues = dimension.Values;
            for (var i = dimensionValues.Count - 1; i >= 0; i--)
            {
                var metric = dimensionValues[i];

                // An observation is at the end of its export interval, which puts it in exactly one chart interval so it
                // can't be counted twice. Values are ordered by start, and an instrument's integer and double values
                // are rolled up separately, so their ends aren't in order. An earlier end doesn't end the search.
                // Compare in nanoseconds: End is truncated to 100ns ticks, which would move an end just past a chart
                // boundary back onto it, into the interval before the one its rollup was grouped into.
                if (metric.EndTimeUnixNano <= startUnixNano || metric.EndTimeUnixNano > endUnixNano)
                {
                    continue;
                }

                var value = metric switch
                {
                    MetricValue<long> longMetric => longMetric.Value,
                    MetricValue<double> doubleMetric => doubleMetric.Value,
                    _ => 0
                };

                // Only values stored by an older dashboard can have a count above one, without their individual times.
                sum.Add(value, metric.Count);
                hasValue = true;
            }
        }

        pointValue = sum.Value;

        // JS interop doesn't support serializing NaN values.
        if (double.IsNaN(pointValue))
        {
            pointValue = default;
            return false;
        }

        return hasValue;
    }

    /// <summary>
    /// Gets the value, or the largest finite value of its sign when it's beyond a double's range. Each stored value is
    /// finite, but sums and differences of them can overflow. JS interop serializes chart values with System.Text.Json,
    /// which throws on infinity, so an overflow would stop the chart from rendering. The largest finite value still draws
    /// as off the scale.
    /// </summary>
    internal static double ToFinite(double value) => Math.Clamp(value, double.MinValue, double.MaxValue);

    internal static List<double?> CalculateCounterIncreases(IReadOnlyList<double?> values, OtlpAggregationTemporality temporality)
    {
        // Delta counters already report the change per export, so their values are the increases.
        if (temporality == OtlpAggregationTemporality.Delta)
        {
            return [.. values];
        }

        // Cumulative counters report a running total, e.g. [10, 15, null, 22, 3] where 3 follows a
        // process restart. The increase is the difference from the last known total, carried across
        // empty intervals: an app exporting every 60s leaves most 10s intervals empty, and its
        // growth should show up in the interval where it was observed, not be dropped.
        // After a reset the total starts again from zero, so the new value itself is the increase.
        var increases = new List<double?>(values.Count);
        double? previous = null;
        foreach (var value in values)
        {
            if (value is not { } current)
            {
                increases.Add(null);
                continue;
            }

            // The first point has nothing to compare against.
            increases.Add(previous is { } last ? (current >= last ? ToFinite(current - last) : current) : null);
            previous = current;
        }

        return increases;
    }

    internal static bool TryCalculatePoint(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, out double pointValue)
    {
        var hasValue = false;
        var sum = new FiniteSum();
        var endUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(end.UtcDateTime);

        foreach (var dimension in dimensions)
        {
            var dimensionValues = dimension.Values;
            var dimensionValue = 0d;
            // The first value replaces the initial zero rather than being compared with it. Gauges and up-down counters
            // can be negative, and comparing -5 with zero would report a level of 0.
            var hasDimensionValue = false;
            for (var i = dimensionValues.Count - 1; i >= 0; i--)
            {
                var metric = dimensionValues[i];
                // MetricValueBase.Start/End are DateTime (Kind=Utc from Unix timestamps).
                // Use explicit DateTimeOffset conversion to avoid silent local-time assumption
                // if a DateTime with Kind=Unspecified is ever stored.
                var metricStart = new DateTimeOffset(metric.Start, TimeSpan.Zero);
                var metricEnd = new DateTimeOffset(metric.End, TimeSpan.Zero);

                // Values are ordered by start, oldest first. An instrument's integer and double values are rolled up
                // separately, so their ends aren't in order and an earlier end doesn't end the search.
                if (metricEnd < start)
                {
                    continue;
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

                // Compare in nanoseconds: Start is truncated to 100ns ticks, which would move a start just past the
                // interval end back onto it, and into this interval.
                if (metric.StartTimeUnixNano <= endUnixNano)
                {
                    var value = metric switch
                    {
                        MetricValue<long> longMetric => longMetric.Value,
                        MetricValue<double> doubleMetric => doubleMetric.Value,
                        _ => 0
                    };

                    dimensionValue = hasDimensionValue ? Math.Max(value, dimensionValue) : value;
                    hasDimensionValue = true;
                    hasValue = true;
                }
            }

            sum.Add(dimensionValue);
        }

        pointValue = sum.Value;

        // JS interop doesn't support serializing NaN values.
        if (double.IsNaN(pointValue))
        {
            pointValue = default;
            return false;
        }

        return hasValue;
    }

    internal static bool TryCalculateHistogramPoints(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, Dictionary<int, ChartTrace> traces, List<ChartExemplar> exemplars, out bool incompatibleBounds)
    {
        var hasValue = TryAggregateHistogram(dimensions, start, end, collectBucketCounts: true, exemplars,
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
        bool collectBucketCounts, List<ChartExemplar>? exemplars,
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
                        CollectExemplars(exemplars, metric);
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

    private static void CollectExemplars(List<ChartExemplar> exemplars, MetricValueBase metric)
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
                if (exemplar.TimeUnixNano == existingExemplar.TimeUnixNano &&
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

            exemplars.Add(new ChartExemplar
            {
                TimeUnixNano = exemplar.TimeUnixNano,
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

    /// <summary>
    /// Adds up values, with a finite total that doesn't depend on their order. Clamping each addition would: for
    /// <c>double.MaxValue</c>, <c>double.MaxValue</c> and <c>-double.MaxValue</c>, it would give 0 in that order and
    /// <c>double.MaxValue</c> with the negative value first.
    /// </summary>
    private struct FiniteSum
    {
        // A power of two, so scaling is exact except for values below about 1e-298, which don't matter next to a total
        // beyond a double's range. 2^32 values of double.MaxValue fit in the scaled total.
        private static readonly double s_scaleDown = Math.ScaleB(1, -32);
        private static readonly double s_scaleUp = Math.ScaleB(1, 32);

        private double _sum;
        private double _scaledSum;

        public void Add(double value, double count = 1)
        {
            _sum += value * count;
            _scaledSum += value * s_scaleDown * count;
        }

        // The total is used as is unless it overflowed, so it's unchanged for values in a double's range.
        public readonly double Value => double.IsFinite(_sum) ? _sum : ToFinite(_scaledSum * s_scaleUp);
    }
}
