// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

/// <summary>
/// Combines histogram distributions by merging buckets at shared explicit boundaries.
/// </summary>
internal static class HistogramBuckets
{
    public static bool Add(ref ulong[]? counts, ref double[]? bounds, ReadOnlySpan<ulong> incomingCounts,
        double[] incomingBounds, ReadOnlySpan<ulong> previousCounts)
    {
        if (!previousCounts.IsEmpty && previousCounts.Length != incomingCounts.Length)
        {
            throw new InvalidOperationException("Cumulative histogram bucket counts changed size.");
        }

        var hasObservations = false;
        for (var i = 0; i < incomingCounts.Length; i++)
        {
            if (incomingCounts[i] != (previousCounts.IsEmpty ? 0 : previousCounts[i]))
            {
                hasObservations = true;
                break;
            }
        }
        if (!hasObservations)
        {
            return false;
        }

        if (counts is null)
        {
            counts = new ulong[incomingCounts.Length];
            bounds = incomingBounds;
        }

        var currentBounds = bounds ?? throw new InvalidOperationException("Histogram bucket counts have no explicit boundaries.");
        if (!currentBounds.AsSpan().SequenceEqual(incomingBounds))
        {
            // Only shared boundaries allow exact merging. Splitting a source bucket would require
            // inventing a distribution within it. OTLP buckets are (lower, upper], plus +Inf:
            // https://opentelemetry.io/docs/specs/otel/metrics/data-model/#histogram
            var sharedBounds = GetSharedBounds(currentBounds, incomingBounds);
            if (!ReferenceEquals(sharedBounds, currentBounds) || counts.Length != sharedBounds.Length + 1)
            {
                var merged = new ulong[sharedBounds.Length + 1];
                AddBuckets(merged, counts, currentBounds, sharedBounds, []);
                counts = merged;
            }
            AddBuckets(counts, incomingCounts, incomingBounds, sharedBounds, previousCounts);
            bounds = sharedBounds;
            return true;
        }

        if (counts.Length != incomingCounts.Length)
        {
            throw new InvalidOperationException("Histogram bucket counts do not match their explicit boundaries.");
        }

        for (var i = 0; i < counts.Length; i++)
        {
            var value = incomingCounts[i] - (previousCounts.IsEmpty ? 0 : previousCounts[i]);
            counts[i] = checked(counts[i] + value);
        }

        return true;
    }

    private static void AddBuckets(ulong[] destination, ReadOnlySpan<ulong> counts, double[] bounds,
        double[] sharedBounds, ReadOnlySpan<ulong> previousCounts)
    {
        var target = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            while (target < sharedBounds.Length && (i >= bounds.Length || bounds[i] > sharedBounds[target]))
            {
                target++;
            }
            var value = counts[i] - (previousCounts.IsEmpty ? 0 : previousCounts[i]);
            destination[target] = checked(destination[target] + value);
        }
    }

    private static double[] GetSharedBounds(double[] left, double[] right)
    {
        // OTLP explicit bounds are strictly increasing, so intersect them with a linear merge.
        // Reuse either existing layout when it is already the common coarser layout.
        var count = 0;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex].Equals(right[rightIndex]))
            {
                count++;
                leftIndex++;
                rightIndex++;
            }
            else if (left[leftIndex] < right[rightIndex])
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        if (count == left.Length)
        {
            return left;
        }
        if (count == right.Length)
        {
            return right;
        }
        if (count == 0)
        {
            return [];
        }

        var shared = new double[count];
        leftIndex = 0;
        rightIndex = 0;
        var sharedIndex = 0;
        while (sharedIndex < count)
        {
            if (left[leftIndex].Equals(right[rightIndex]))
            {
                shared[sharedIndex++] = left[leftIndex];
                leftIndex++;
                rightIndex++;
            }
            else if (left[leftIndex] < right[rightIndex])
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return shared;
    }
}
