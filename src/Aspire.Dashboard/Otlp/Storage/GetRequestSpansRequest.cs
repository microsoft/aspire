// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Selects the incoming request spans (server and consumer spans) that started in a time window.
/// </summary>
public sealed class GetRequestSpansRequest
{
    /// <summary>
    /// Gets the resources whose spans are returned. Empty returns spans of every resource.
    /// </summary>
    public required IReadOnlyList<ResourceKey> ResourceKeys { get; init; }

    /// <summary>
    /// Gets the inclusive UTC start of the window.
    /// </summary>
    public required DateTime StartTime { get; init; }

    /// <summary>
    /// Gets the exclusive UTC end of the window.
    /// </summary>
    public required DateTime EndTime { get; init; }

    /// <summary>
    /// Gets the most spans returned. The newest spans are kept when the window has more.
    /// </summary>
    public required int MaxCount { get; init; }
}
