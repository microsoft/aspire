// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Components;

internal static class MetricInstrumentDataCache
{
    public static List<MetricDimensionCursor> CreateCursors(OtlpInstrumentData data, TimeSpan refreshLookback, TimeSpan dataPointInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(refreshLookback, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dataPointInterval, TimeSpan.Zero);

        var cursors = new List<MetricDimensionCursor>(data.Dimensions.Count);
        foreach (var dimension in data.Dimensions)
        {
            if (dimension.Values.Count == 0)
            {
                continue;
            }

            var latestValue = dimension.Values[^1];
            // Refresh recent complete buckets because data can arrive behind the latest displayed value. Starting from
            // the rolled value's start when it is earlier also retains source points that determine its representative.
            var lookbackStartTicks = latestValue.End.Ticks - Math.Min(latestValue.End.Ticks, refreshLookback.Ticks);
            var alignedLookbackStartTicks = lookbackStartTicks - (lookbackStartTicks % dataPointInterval.Ticks);
            cursors.Add(new MetricDimensionCursor
            {
                Attributes = dimension.Attributes,
                StartTime = new DateTime(Math.Min(latestValue.Start.Ticks, alignedLookbackStartTicks), DateTimeKind.Utc)
            });
        }
        return cursors;
    }

    public static OtlpInstrumentData Merge(
        OtlpInstrumentData cached,
        OtlpInstrumentData refreshed,
        IReadOnlyList<MetricDimensionCursor> cursors,
        DateTime windowStart)
    {
        var dimensions = new List<DimensionScope>(refreshed.Dimensions.Count);
        foreach (var refreshedDimension in refreshed.Dimensions)
        {
            var cachedDimension = cached.Dimensions.FirstOrDefault(dimension => AttributesEqual(dimension.Attributes, refreshedDimension.Attributes));
            var cursor = cursors.FirstOrDefault(cursor => AttributesEqual(cursor.Attributes, refreshedDimension.Attributes));
            if (cachedDimension is null || cursor is null)
            {
                dimensions.Add(CloneDimension(refreshedDimension, windowStart));
                continue;
            }

            var mergedDimension = new DimensionScope(refreshedDimension.Capacity, refreshedDimension.Attributes);
            foreach (var value in cachedDimension.Values)
            {
                if (value.End >= windowStart && value.End < cursor.StartTime)
                {
                    mergedDimension.Values.Add(MetricValueBase.Clone(value));
                }
            }
            foreach (var value in refreshedDimension.Values)
            {
                if (value.End >= windowStart)
                {
                    mergedDimension.Values.Add(MetricValueBase.Clone(value));
                }
            }
            dimensions.Add(mergedDimension);
        }

        return new OtlpInstrumentData
        {
            Summary = refreshed.Summary,
            Dimensions = dimensions,
            KnownAttributeValues = refreshed.KnownAttributeValues,
            HasOverflow = refreshed.HasOverflow
        };
    }

    private static DimensionScope CloneDimension(DimensionScope dimension, DateTime windowStart)
    {
        var clone = new DimensionScope(dimension.Capacity, dimension.Attributes);
        foreach (var value in dimension.Values)
        {
            if (value.End >= windowStart)
            {
                clone.Values.Add(MetricValueBase.Clone(value));
            }
        }
        return clone;
    }

    private static bool AttributesEqual(KeyValuePair<string, string>[] left, KeyValuePair<string, string>[] right)
        => left.AsSpan().SequenceEqual(right);
}