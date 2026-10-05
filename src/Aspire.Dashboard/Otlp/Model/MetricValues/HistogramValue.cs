// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Extensions;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

public class HistogramValue : MetricValueBase
{
    public ulong[] Values { get; init; }
    public double Sum { get; init; }
    public double[] ExplicitBounds { get; init; }

    /// <summary>
    /// Gets the producer's aggregation start time, independently of the chart interval.
    /// </summary>
    public DateTime AggregationStart { get; init; }

    internal long AggregationId { get; init; }
    internal OtlpAggregationTemporality AggregationTemporality { get; init; }

    /// <summary>
    /// Initializes a histogram snapshot with its chart interval and aggregation identity.
    /// </summary>
    /// <param name="values">The counts in each histogram bucket.</param>
    /// <param name="sum">The sum of recorded measurements.</param>
    /// <param name="count">The number of recorded measurements.</param>
    /// <param name="start">The start of the chart interval.</param>
    /// <param name="end">The end of the data point.</param>
    /// <param name="explicitBounds">The explicit upper bucket boundaries.</param>
    /// <param name="aggregationStart">The aggregation start reported by the producer.</param>
    /// <param name="aggregationId">The identity separating cumulative aggregations or delta intervals.</param>
    /// <param name="aggregationTemporality">The producer's aggregation temporality.</param>
    public HistogramValue(ulong[] values, double sum, ulong count, DateTime start, DateTime end, double[] explicitBounds,
        DateTime aggregationStart, long aggregationId, OtlpAggregationTemporality aggregationTemporality) : base(start, end)
    {
        Values = values;
        Sum = sum;
        Count = count;
        ExplicitBounds = explicitBounds;
        AggregationStart = aggregationStart;
        AggregationId = aggregationId;
        AggregationTemporality = aggregationTemporality;
    }

    internal static HistogramValue Create(HistogramDataPoint point, OtlpAggregationTemporality temporality, HistogramValue? previous)
    {
        OtlpHelpers.ValidateHistogramDataPoint(point);
        var aggregationStart = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
        var end = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano);
        var bounds = previous is not null && previous.ExplicitBounds.SequenceEqual(point.ExplicitBounds)
            ? previous.ExplicitBounds
            : point.ExplicitBounds.ToArray();
        var counts = point.BucketCounts.ToArray();
        var start = aggregationStart;
        var aggregationId = end.Ticks;

        if (temporality != OtlpAggregationTemporality.Delta && previous is not null)
        {
            var reset = aggregationStart != previous.AggregationStart || point.Count < previous.Count;
            if (!reset)
            {
                if (!previous.ExplicitBounds.AsSpan().SequenceEqual(bounds) || previous.Values.Length != counts.Length)
                {
                    throw new InvalidOperationException("Histogram bucket layout changed within a cumulative aggregation.");
                }

                // A producer that omits start timestamps can still reset. Detect decreasing bucket
                // counts before unsigned subtraction, even if the total count has already recovered.
                for (var i = 0; i < counts.Length; i++)
                {
                    reset |= counts[i] < previous.Values[i];
                }
            }

            start = reset && aggregationStart > previous.End ? aggregationStart : previous.End;
            aggregationId = reset ? end.Ticks : previous.AggregationId;
        }

        return new HistogramValue(counts, point.Sum, point.Count, start, end, bounds, aggregationStart, aggregationId, temporality);
    }

    internal bool CanMerge(HistogramValue other) =>
        AggregationTemporality != OtlpAggregationTemporality.Delta &&
        AggregationId == other.AggregationId &&
        Count == other.Count &&
        Sum.Equals(other.Sum) &&
        Values.AsSpan().SequenceEqual(other.Values) &&
        ExplicitBounds.AsSpan().SequenceEqual(other.ExplicitBounds);

    public override string ToString()
    {
        var sb = new StringBuilder();
        var first = true;
        sb.Append(CultureInfo.InvariantCulture, $"Count:{Count} Sum:{Sum} Values:");
        foreach (var v in Values)
        {
            if (!first)
            {
                sb.Append(' ');
            }
            first = false;
            sb.Append(CultureInfo.InvariantCulture, $"{v}");
        }
        return sb.ToString();
    }

    internal override bool TryCompare(MetricValueBase other, out int comparisonResult)
    {
        comparisonResult = default;
        return false;
    }

    protected override MetricValueBase Clone()
    {
        var value = new HistogramValue(Values, Sum, Count, Start, End, ExplicitBounds, AggregationStart, AggregationId, AggregationTemporality);
        if (HasExemplars)
        {
            value.Exemplars.AddRange(Exemplars);
        }
        return value;
    }

    public override bool Equals(object? obj)
    {
        return obj is HistogramValue other
            && Values.Equivalent(other.Values)
            && Sum.Equals(other.Sum)
            && Count.Equals(other.Count)
            && Start.Equals(other.Start)
            && AggregationStart.Equals(other.AggregationStart)
            && AggregationId == other.AggregationId
            && AggregationTemporality == other.AggregationTemporality
            && ExplicitBounds.Equivalent(other.ExplicitBounds);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Start, Count, Values, Sum, ExplicitBounds, AggregationStart, AggregationId, AggregationTemporality);
    }
}
