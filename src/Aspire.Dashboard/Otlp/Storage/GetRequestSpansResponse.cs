// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// The request spans of a time window, newest first.
/// </summary>
public sealed class GetRequestSpansResponse
{
    public required IReadOnlyList<RequestSpanSummary> Items { get; init; }

    /// <summary>
    /// Gets the number of request spans in the window, which is larger than the item count when the window was capped.
    /// </summary>
    public required int TotalItemCount { get; init; }
}
