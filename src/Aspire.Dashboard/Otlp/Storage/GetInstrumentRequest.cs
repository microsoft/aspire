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

    /// <summary>
    /// Gets the per-dimension boundaries from which data should be refreshed. Dimensions without a cursor use <see cref="StartTime"/>.
    /// </summary>
    public IReadOnlyList<MetricDimensionCursor> DimensionCursors { get; init; } = [];

    /// <summary>
    /// Gets the interval used to roll up data points within each dimension. A <see langword="null"/> value returns full-fidelity data.
    /// </summary>
    public TimeSpan? DataPointInterval { get; init; }

    /// <summary>
    /// Gets a value indicating whether exemplar attributes should be populated.
    /// </summary>
    public bool PopulateExemplarAttributes { get; init; } = true;
}

/// <summary>
/// Specifies the boundary from which one metric dimension should be refreshed.
/// </summary>
public sealed class MetricDimensionCursor
{
    /// <summary>
    /// Gets the attributes that identify the dimension.
    /// </summary>
    public required KeyValuePair<string, string>[] Attributes { get; init; }

    /// <summary>
    /// Gets the inclusive beginning of the range to refresh.
    /// </summary>
    public required DateTime StartTime { get; init; }
}
