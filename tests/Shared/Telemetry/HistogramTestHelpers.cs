// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry.Proto.Metrics.V1;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Tests.Shared.Telemetry;

internal static class HistogramTestHelpers
{
    public static HistogramDataPoint CreatePoint(DateTime start, DateTime end, ulong[] counts, double[] bounds)
    {
        var count = counts.Aggregate(0ul, (total, value) => checked(total + value));
        var point = new HistogramDataPoint
        {
            StartTimeUnixNano = DateTimeToUnixNanoseconds(start),
            TimeUnixNano = DateTimeToUnixNanoseconds(end),
            Count = count,
            Sum = count
        };
        point.BucketCounts.AddRange(counts);
        point.ExplicitBounds.AddRange(bounds);
        return point;
    }

    public static ResourceMetrics CreateMetrics(AggregationTemporality temporality, params HistogramDataPoint[] points)
    {
        var histogram = new Histogram { AggregationTemporality = temporality };
        histogram.DataPoints.AddRange(points);
        return new ResourceMetrics
        {
            Resource = CreateResource(),
            ScopeMetrics =
            {
                new ScopeMetrics
                {
                    Scope = CreateScope("test-meter"),
                    Metrics = { new Metric { Name = "histogram", Histogram = histogram } }
                }
            }
        };
    }
}
