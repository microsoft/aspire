// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model.ManageData;

/// <summary>
/// Represents a row in the manage data grid, containing resource information and nested data rows.
/// </summary>
public sealed class ResourceDataRow
{
    /// <summary>
    /// The ResourceViewModel from the dashboard client. May be null for telemetry-only resources.
    /// </summary>
    public ResourceViewModel? Resource { get; init; }

    /// <summary>
    /// The OtlpResource from telemetry. May be null if no telemetry data exists yet.
    /// </summary>
    public OtlpResource? OtlpResource { get; init; }

    /// <summary>
    /// The display name for this resource row.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets telemetry data for this resource row.
    /// </summary>
    public List<TelemetryDataRow> TelemetryData { get; set; } = [];

    /// <summary>
    /// Gets whether this resource is telemetry-only (no corresponding ResourceViewModel).
    /// </summary>
    public bool IsTelemetryOnly => Resource is null && OtlpResource is not null;
}
