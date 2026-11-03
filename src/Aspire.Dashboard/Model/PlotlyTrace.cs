// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public class PlotlyTrace
{
    public required string Name { get; init; }
    public required List<DateTimeOffset> X { get; init; }
    public required List<double?> Y { get; init; }
    public required List<string?> Tooltips { get; init; }
    public required List<PlotlyTraceData> TraceData { get; init; }
}

public sealed record PlotlyTraceData(string? TraceId, string? SpanId);

public class PlotlyUserLocale
{
    public required string Time { get; init; }
    public required List<string> Periods { get; init; }
}
