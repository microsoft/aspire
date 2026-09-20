// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Contains the trace information displayed on the traces page.
/// </summary>
public sealed class TraceSummary
{
    public required string TraceId { get; init; }
    public required string FullName { get; init; }
    public required DateTime StartTime { get; init; }
    public required TimeSpan Duration { get; init; }
    public required OtlpResource RootResource { get; init; }
    public required IReadOnlyList<TraceResourceSummary> Resources { get; init; }
    public required bool HasError { get; init; }
    public required bool HasGenAI { get; init; }
}

/// <summary>
/// Contains the resource information displayed for a trace on the traces page.
/// </summary>
public sealed class TraceResourceSummary
{
    public required OtlpResource Resource { get; init; }
    public required int TotalSpans { get; init; }
    public required int ErroredSpans { get; init; }
}