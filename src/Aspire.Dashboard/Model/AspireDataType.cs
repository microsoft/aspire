// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Represents the type of data in an Aspire resource.
/// </summary>
public enum AspireDataType
{
    /// <summary>
    /// Console logs from resource stdout/stderr.
    /// </summary>
    ConsoleLogs,

    /// <summary>
    /// Structured logs from OpenTelemetry.
    /// </summary>
    StructuredLogs,

    /// <summary>
    /// Distributed traces from OpenTelemetry.
    /// </summary>
    Traces,

    /// <summary>
    /// Metrics from OpenTelemetry.
    /// </summary>
    Metrics,

    /// <summary>
    /// Resource details from the dashboard client.
    /// </summary>
    ResourceDetails,

    /// <summary>
    /// Indicates the resource itself should be removed (all available telemetry types were selected).
    /// </summary>
    Resource
}
