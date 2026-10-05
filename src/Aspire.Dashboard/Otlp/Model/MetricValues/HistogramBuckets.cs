// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

/// <summary>
/// Combines histogram distributions by merging buckets at shared explicit boundaries.
/// </summary>
internal static class HistogramBuckets
{
    public static void Add(ref ulong[]? counts, ref double[]? bounds, ulong[] incomingCounts, double[] incomingBounds)
    {
        if (counts is null)
        {
            counts = incomingCounts.ToArray();
            bounds = incomingBounds;
            return;
        }

        var currentBounds = bounds ?? throw new InvalidOperationException("Histogram bucket counts have no explicit boundaries.");
        if (!currentBounds.AsSpan().SequenceEqual(incomingBounds))
        {
            // Only shared boundaries allow exact merging. Splitting a source bucket would require
            // inventing a distribution within it. OTLP buckets are (lower, upper], plus +Inf:
            // https://opentelemetry.io/docs/specs/otel/metrics/data-model/#histogram
            var sharedBounds = currentBounds.Intersect(incomingBounds).ToArray();
            counts = MergeBuckets(counts, currentBounds, sharedBounds);
            incomingCounts = MergeBuckets(incomingCounts, incomingBounds, sharedBounds);
            bounds = sharedBounds;
        }

        if (counts.Length != incomingCounts.Length)
        {
            throw new InvalidOperationException("Histogram bucket counts do not match their explicit boundaries.");
        }

        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = checked(counts[i] + incomingCounts[i]);
        }
    }

    private static ulong[] MergeBuckets(ulong[] counts, double[] bounds, double[] sharedBounds)
    {
        var merged = new ulong[sharedBounds.Length + 1];
        var target = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            while (target < sharedBounds.Length && (i >= bounds.Length || bounds[i] > sharedBounds[target]))
            {
                target++;
            }
            merged[target] = checked(merged[target] + counts[i]);
        }

        return merged;
    }
}
