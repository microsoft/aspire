// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed class WatchLogsRequest
{
    public required ResourceKey? ResourceKey { get; init; }
    public required List<TelemetryFilter> Filters { get; init; }
    public string[]? TextFragments { get; init; }
}
