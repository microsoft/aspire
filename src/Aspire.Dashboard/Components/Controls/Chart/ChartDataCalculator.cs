// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model.MetricValues;

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
            Exemplars = []
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
        DateTimeOffset? lastPointStartTime = null;

        // Generate the points in reverse order so that the chart is drawn from right to left.
        // Add a couple of extra points to the end so that the chart is drawn all the way to the right edge.
        for (var pointIndex = 0; pointIndex < (_pointCount + 2); pointIndex++)
        {
            var start = CalcOffset(pointIndex, startTime, pointDuration);
            var end = CalcOffset(pointIndex - 1, startTime, pointDuration);
            lastPointStartTime = start;

            xValues.Add(toLocal(end));

            if (!TryCalculateHistogramPoints(dimensions, start, end, traces, exemplars, toLocal))
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

        ChartTrace? previousValues = null;
        foreach (var trace in traces.OrderBy(kvp => kvp.Key))
        {
            var currentTrace = trace.Value;

            for (var i = 0; i < currentTrace.Values.Count; i++)
            {
                double? diffValue = (previousValues != null)
                    ? currentTrace.Values[i] - previousValues.Values[i] ?? 0
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
            Exemplars = exemplars
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

                if (metricStart <= end)
                {
                    var value = metric switch
                    {
                        MetricValue<long> longMetric => longMetric.Value,
                        MetricValue<double> doubleMetric => doubleMetric.Value,
                        HistogramValue histogramValue => histogramValue.Count,
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

    internal static bool TryCalculateHistogramPoints(List<DimensionScope> dimensions, DateTimeOffset start, DateTimeOffset end, Dictionary<int, ChartTrace> traces, List<ChartExemplar> exemplars, Func<DateTimeOffset, DateTimeOffset> toLocal)
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
                // MetricValueBase.Start is DateTime (Kind=Utc from Unix timestamps).
                // Use explicit DateTimeOffset conversion to avoid silent local-time assumption.
                var metricStart = new DateTimeOffset(metric.Start, TimeSpan.Zero);
                if (metricStart >= start && metricStart <= end)
                {
                    var histogramValue = GetHistogramValue(metric);

                    CollectExemplars(exemplars, metric, toLocal);

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

        if (totalCount == 0)
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
