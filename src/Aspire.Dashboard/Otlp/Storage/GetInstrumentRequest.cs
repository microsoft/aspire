// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Specifies the instrument data to retrieve from a telemetry repository.
/// </summary>
public sealed class GetInstrumentRequest
{
    /// <summary>
    /// Gets the instrument name.
    /// </summary>
    public required string InstrumentName { get; init; }

    /// <summary>
    /// Gets the resource that emitted the instrument data.
    /// </summary>
    public required ResourceKey ResourceKey { get; init; }

    /// <summary>
    /// Gets the meter name.
    /// </summary>
    public required string MeterName { get; init; }

    /// <summary>
    /// Gets the beginning of the time range to retrieve.
    /// </summary>
    public DateTime? StartTime { get; init; }

    /// <summary>
    /// Gets the end of the time range to retrieve.
    /// </summary>
    public DateTime? EndTime { get; init; }

    /// <summary>
    /// Gets the dimension values to retrieve, keyed by dimension name. An empty dictionary retrieves all dimensions.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string?>> DimensionFilters { get; init; } = new Dictionary<string, IReadOnlyList<string?>>();
}
