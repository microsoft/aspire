// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.HealthModels;

/// <summary>
/// The health state of an entity or signal in a health model.
/// </summary>
/// <remarks>
/// <para>
/// These values intentionally mirror the <c>HealthState</c> enum of Azure Monitor health models so a model
/// defined locally in the dashboard can be translated to a <c>Microsoft.CloudHealth/healthmodels</c> deployment.
/// The Azure enum also contains <c>Deleted</c>, which is a service-side lifecycle artifact with no local
/// equivalent, so it is not represented here.
/// </para>
/// <para>
/// See https://learn.microsoft.com/azure/azure-monitor/health-models/concepts.
/// </para>
/// </remarks>
public enum HealthState
{
    /// <summary>No signal has reported yet, or the entity has no signals to evaluate.</summary>
    Unknown = 0,

    /// <summary>All signals are within their expected range.</summary>
    Healthy = 1,

    /// <summary>At least one signal breached its degraded threshold but not its unhealthy threshold.</summary>
    Degraded = 2,

    /// <summary>At least one signal breached its unhealthy threshold.</summary>
    Unhealthy = 3
}
