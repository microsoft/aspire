// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Contains a page of trace summaries and aggregate information for the matching traces.
/// </summary>
public sealed class GetTraceSummariesResponse
{
    public required PagedResult<TraceSummary> PagedResult { get; init; }
    public required TimeSpan MaxDuration { get; init; }
}