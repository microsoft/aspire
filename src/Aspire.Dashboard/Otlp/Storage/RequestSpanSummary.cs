// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// The fields of an incoming request span that request activity summaries use, read without materializing its trace.
/// </summary>
public sealed class RequestSpanSummary
{
    public required OtlpResource Resource { get; init; }
    public required string Name { get; init; }
    public required DateTime StartTime { get; init; }
    public required TimeSpan Duration { get; init; }
    public required OtlpSpanStatusCode Status { get; init; }

    /// <summary>
    /// Gets the span's <c>http.route</c> attribute, when it has one.
    /// </summary>
    public string? HttpRoute { get; init; }

    /// <summary>
    /// Gets the span's <c>http.request.method</c> attribute, or the older <c>http.method</c> attribute.
    /// </summary>
    public string? HttpMethod { get; init; }
}
