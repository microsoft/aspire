// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Dashboard.Otlp.Model.MetricValues;

[DebuggerDisplay("Start = {Start}, End = {End}, Exemplars = {Exemplars.Count}")]
public abstract class MetricValueBase
{
    private List<MetricsExemplar>? _exemplars;

    public List<MetricsExemplar> Exemplars => _exemplars ??= new List<MetricsExemplar>();
    public bool HasExemplars => _exemplars != null && _exemplars.Count > 0;
    public DateTime Start => OtlpHelpers.UnixNanoSecondsToDateTime(StartTimeUnixNano);
    public DateTime End => OtlpHelpers.UnixNanoSecondsToDateTime(EndTimeUnixNano);

    /// <summary>
    /// Gets the normalized interval start in nanoseconds since the Unix epoch.
    /// </summary>
    public ulong StartTimeUnixNano { get; }

    /// <summary>
    /// Gets or sets the point end in nanoseconds since the Unix epoch.
    /// </summary>
    public ulong EndTimeUnixNano { get; set; }
    public ulong Count = 1;

    protected MetricValueBase(ulong startTimeUnixNano, ulong endTimeUnixNano)
    {
        StartTimeUnixNano = startTimeUnixNano;
        EndTimeUnixNano = endTimeUnixNano;
    }

    internal static MetricValueBase Clone(MetricValueBase item)
    {
        return item.Clone();
    }

    internal abstract bool TryCompare(MetricValueBase other, out int comparisonResult);

    protected abstract MetricValueBase Clone();
}

[DebuggerDisplay("Start = {Start}, Value = {Value}, SpanId = {SpanId}, TraceId = {TraceId}, Attributes = {Attributes.Count}")]
public sealed class MetricsExemplar
{
    public DateTime Start => OtlpHelpers.UnixNanoSecondsToDateTime(TimeUnixNano);

    /// <summary>
    /// Gets the exemplar timestamp in nanoseconds since the Unix epoch.
    /// </summary>
    public required ulong TimeUnixNano { get; init; }
    public required double Value { get; init; }
    public required string SpanId { get; init; }
    public required string TraceId { get; init; }
    public required KeyValuePair<string, string>[] Attributes { get; init; }
}
