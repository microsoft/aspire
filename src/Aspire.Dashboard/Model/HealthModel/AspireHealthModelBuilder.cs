// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Dashboard.Model.HealthModel;

/// <summary>
/// Maps Aspire resource state into <see cref="HealthState"/>, shared by the resource graph's health rollup.
/// </summary>
public static class AspireHealthModelBuilder
{
    /// <summary>
    /// Maps an Aspire resource lifecycle state to a health state.
    /// </summary>
    /// <remarks>
    /// Transient states map to <see cref="HealthState.Unknown"/> rather than to a non-healthy state. Because
    /// unknown is the least severe state under a worst-of rollup, a service that is still starting does not
    /// make the whole application look broken.
    /// </remarks>
    internal static HealthState MapResourceState(KnownResourceState? state) => state switch
    {
        KnownResourceState.Running or KnownResourceState.Finished => HealthState.Healthy,

        KnownResourceState.FailedToStart
            or KnownResourceState.Exited
            or KnownResourceState.RuntimeUnhealthy
            or KnownResourceState.ValueMissing => HealthState.Unhealthy,

        _ => HealthState.Unknown
    };

    /// <summary>Maps a health check result to a health state.</summary>
    internal static HealthState MapHealthStatus(HealthStatus? status) => status switch
    {
        HealthStatus.Healthy => HealthState.Healthy,
        HealthStatus.Degraded => HealthState.Degraded,
        HealthStatus.Unhealthy => HealthState.Unhealthy,
        _ => HealthState.Unknown
    };
}
