// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

[DebuggerDisplay("Name = {Name}, Values = {Values.Count}")]
public class DimensionScope
{
    public const string NoDimensions = "no-dimensions";
    public string Name { get; init; }
    public KeyValuePair<string, string>[] Attributes { get; init; }
    public IList<MetricValueBase> Values => _values;

    private readonly CircularBuffer<MetricValueBase> _values;
    // Used to aid in merging values that are the same in a concurrent environment
    private MetricValueBase? _lastValue;

    public int Capacity => _values.Capacity;

    public DimensionScope(int capacity, KeyValuePair<string, string>[] attributes)
    {
        Attributes = attributes;
        var name = Attributes.ConcatProperties();
        Name = name != null && name.Length > 0 ? name : NoDimensions;
        _values = new(capacity);
    }

    public void AddPointValue(NumberDataPoint d, OtlpContext context)
    {
        var start = OtlpHelpers.UnixNanoSecondsToDateTime(d.StartTimeUnixNano);
        var end = OtlpHelpers.UnixNanoSecondsToDateTime(d.TimeUnixNano);

        if (d.ValueCase == NumberDataPoint.ValueOneofCase.AsInt)
        {
            var value = d.AsInt;
            var lastLongValue = _lastValue as MetricValue<long>;
            if (lastLongValue is not null && lastLongValue.Value == value)
            {
                lastLongValue.End = end;
                AddExemplars(lastLongValue, d.Exemplars, context);
                Interlocked.Increment(ref lastLongValue.Count);
            }
            else
            {
                if (lastLongValue is not null)
                {
                    start = lastLongValue.End;
                }
                _lastValue = new MetricValue<long>(d.AsInt, start, end);
                AddExemplars(_lastValue, d.Exemplars, context);
                _values.Add(_lastValue);
            }
        }
        else if (d.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble)
        {
            var lastDoubleValue = _lastValue as MetricValue<double>;
            if (lastDoubleValue is not null && lastDoubleValue.Value == d.AsDouble)
            {
                lastDoubleValue.End = end;
                AddExemplars(lastDoubleValue, d.Exemplars, context);
                Interlocked.Increment(ref lastDoubleValue.Count);
            }
            else
            {
                if (lastDoubleValue is not null)
                {
                    start = lastDoubleValue.End;
                }
                _lastValue = new MetricValue<double>(d.AsDouble, start, end);
                AddExemplars(_lastValue, d.Exemplars, context);
                _values.Add(_lastValue);
            }
        }
    }

    public void AddHistogramValue(HistogramDataPoint h, OtlpContext context)
    {
        var start = OtlpHelpers.UnixNanoSecondsToDateTime(h.StartTimeUnixNano);
        var end = OtlpHelpers.UnixNanoSecondsToDateTime(h.TimeUnixNano);

        if (h.BucketCounts.Count > 0 && h.ExplicitBounds.Count == 0)
        {
            throw new InvalidOperationException("Histogram data point has bucket counts without any explicit bounds.");
        }

        var lastHistogramValue = _lastValue as HistogramValue;
        if (lastHistogramValue is not null && lastHistogramValue.Values.Length != h.BucketCounts.Count)
        {
            // Histogram bucket layouts must remain stable within a series so cumulative values can
            // be subtracted and combined. A changed bucket count would make the series unusable.
            throw new InvalidOperationException("Histogram data point bucket count length changed.");
        }

        if (lastHistogramValue is not null && lastHistogramValue.Count == h.Count)
        {
            lastHistogramValue.End = end;
            AddExemplars(lastHistogramValue, h.Exemplars, context);
        }
        else
        {
            // If the explicit bounds are the same as the last value, reuse them.
            double[] explicitBounds;
            if (lastHistogramValue is not null)
            {
                start = lastHistogramValue.End;
                explicitBounds = lastHistogramValue.ExplicitBounds.SequenceEqual(h.ExplicitBounds)
                    ? lastHistogramValue.ExplicitBounds
                    : h.ExplicitBounds.ToArray();
            }
            else
            {
                explicitBounds = h.ExplicitBounds.ToArray();
            }

            var bucketCounts = h.BucketCounts.ToArray();
            _lastValue = new HistogramValue(bucketCounts, h.Sum, h.Count, start, end, explicitBounds);
            AddExemplars(_lastValue, h.Exemplars, context);
            _values.Add(_lastValue);
        }
    }

    private static void AddExemplars(MetricValueBase value, RepeatedField<Exemplar> exemplars, OtlpContext context)
    {
        if (exemplars.Count > 0)
        {
            foreach (var exemplar in exemplars)
            {
                // Can't do anything useful with exemplars without a linked trace. Filter them out.
                if (exemplar.TraceId == null || exemplar.SpanId == null)
                {
                    continue;
                }

                var start = OtlpHelpers.UnixNanoSecondsToDateTime(exemplar.TimeUnixNano);
                var exemplarValue = exemplar.HasAsDouble ? exemplar.AsDouble : exemplar.AsInt;

                var exists = false;
                foreach (var existingExemplar in value.Exemplars)
                {
                    if (start == existingExemplar.Start && exemplarValue == existingExemplar.Value)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                {
                    continue;
                }

                value.Exemplars.Add(new MetricsExemplar
                {
                    Start = start,
                    Value = exemplarValue,
                    Attributes = exemplar.FilteredAttributes.ToKeyValuePairs(context),
                    SpanId = exemplar.SpanId.ToHexString(),
                    TraceId = exemplar.TraceId.ToHexString()
                });
            }
        }
    }

    internal static DimensionScope Clone(DimensionScope value, DateTime? valuesStart, DateTime? valuesEnd)
    {
        var newDimensionScope = new DimensionScope(value.Capacity, value.Attributes);
        if (valuesStart != null && valuesEnd != null)
        {
            foreach (var item in value._values)
            {
                if ((item.Start <= valuesEnd.Value && item.End >= valuesStart.Value) ||
                    (item.Start >= valuesStart.Value && item.End <= valuesEnd.Value))
                {
                    newDimensionScope._values.Add(MetricValueBase.Clone(item));
                }
            }
        }

        return newDimensionScope;
    }
}
