// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

[DebuggerDisplay("Start = {Start}, End = {End}, Value = {Value}, Exemplars = {Exemplars.Count}")]
public class MetricValue<T> : MetricValueBase where T : struct
{
    public readonly T Value;

    public MetricValue(T value, DateTime start, DateTime end)
        : this(value, OtlpHelpers.DateTimeToUnixNanoseconds(start), OtlpHelpers.DateTimeToUnixNanoseconds(end))
    {
    }

    /// <summary>
    /// Initializes a numeric metric value with a nanosecond interval.
    /// </summary>
    /// <param name="value">The measurement value.</param>
    /// <param name="startTimeUnixNano">The normalized interval start in nanoseconds since the Unix epoch.</param>
    /// <param name="endTimeUnixNano">The point end in nanoseconds since the Unix epoch.</param>
    public MetricValue(T value, ulong startTimeUnixNano, ulong endTimeUnixNano) : base(startTimeUnixNano, endTimeUnixNano)
    {
        Value = value;
    }

    public override string? ToString() => Value.ToString();

    protected override MetricValueBase Clone()
    {
        var value = new MetricValue<T>(Value, StartTimeUnixNano, EndTimeUnixNano);
        if (HasExemplars)
        {
            value.Exemplars.AddRange(Exemplars);
        }
        return value;
    }

    internal override bool TryCompare(MetricValueBase obj, out int comparisonResult)
    {
        if (Value is IComparable a && obj is MetricValue<T> other)
        {
            comparisonResult = a.CompareTo(other.Value);
            return true;
        }

        comparisonResult = default;
        return false;
    }

    public override bool Equals(object? obj)
    {
        return obj is MetricValue<T> other
            && StartTimeUnixNano == other.StartTimeUnixNano
            && Count == other.Count
            && Equals(Value, other.Value);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(StartTimeUnixNano, Count, Value);
    }
}
