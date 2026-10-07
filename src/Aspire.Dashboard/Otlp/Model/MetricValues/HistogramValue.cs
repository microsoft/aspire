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

    /// <summary>
    /// Creates a snapshot from a validated point, extending and returning the previous instance when cumulative values are unchanged.
    /// </summary>
    internal static HistogramValue Create(HistogramDataPoint point, OtlpAggregationTemporality temporality, HistogramValue? previous)
    {
        var aggregationStart = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
        var end = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano);

        // Out-of-order delivery is not a reset. Reject before changing the aggregation
        // identity or extending the previous snapshot, which would move its end backwards.
        if (temporality != OtlpAggregationTemporality.Delta && previous is not null && end < previous.End)
        {
            throw new InvalidOperationException("Cumulative histogram point timestamp is earlier than the previous point.");
        }

        var sameBounds = previous is not null && HasSameBounds(previous.ExplicitBounds, point);
        // StartTimeUnixNano is optional. With StartTimeUnixNano = 0, use the end timestamp
        // for delta chart placement, but retain the original aggregation start for export.
        // See https://opentelemetry.io/docs/specs/otel/metrics/data-model/#timestamps.
        var start = temporality == OtlpAggregationTemporality.Delta && point.StartTimeUnixNano == 0 ? end : aggregationStart;
        // Delta ends 40 ns and 80 ns into the same tick must have distinct identities.
        // The unchecked cast preserves all timestamp bits in an opaque signed database key.
        var aggregationId = temporality == OtlpAggregationTemporality.Delta
            ? unchecked((long)point.TimeUnixNano)
            : end.Ticks;

        if (temporality != OtlpAggregationTemporality.Delta && previous is not null)
        {
            var reset = aggregationStart != previous.AggregationStart || point.Count < previous.Count;
            if (!reset)
            {
                if (!sameBounds || previous.Values.Length != point.BucketCounts.Count)
                {
                    throw new InvalidOperationException("Histogram bucket layout changed within a cumulative aggregation.");
                }

                // A producer that omits start timestamps can still reset. Detect decreasing bucket
                // counts before unsigned subtraction, even if the total count has already recovered.
                var sameCounts = true;
                for (var i = 0; i < point.BucketCounts.Count; i++)
                {
                    var count = point.BucketCounts[i];
                    reset |= count < previous.Values[i];
                    sameCounts &= count == previous.Values[i];
                }

                // Unchanged cumulative points extend the existing snapshot. Compare protobuf fields
                // before allocating a new bucket array or a snapshot that would immediately be discarded.
                if (!reset && sameCounts && point.Count == previous.Count && point.Sum.Equals(previous.Sum) &&
                    previous.AggregationTemporality != OtlpAggregationTemporality.Delta)
                {
                    previous.End = end;
                    return previous;
                }
            }

            start = reset && aggregationStart > previous.End ? aggregationStart : previous.End;
            // Resets can share an end tick or even an exact timestamp. Advance past the persisted
            // epoch identity so those resets remain distinct, including after database reopening.
            aggregationId = reset ? Math.Max(end.Ticks, checked(previous.AggregationId + 1)) : previous.AggregationId;
        }

        var bounds = previous is not null && sameBounds ? previous.ExplicitBounds : point.ExplicitBounds.ToArray();
        return new HistogramValue(point.BucketCounts.ToArray(), point.Sum, point.Count, start, end, bounds, aggregationStart, aggregationId, temporality);
    }

    private static bool HasSameBounds(double[] bounds, HistogramDataPoint point)
    {
        if (bounds.Length != point.ExplicitBounds.Count)
        {
            return false;
        }

        for (var i = 0; i < bounds.Length; i++)
        {
            if (!bounds[i].Equals(point.ExplicitBounds[i]))
            {
                return false;
            }
        }

        return true;
    }

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
