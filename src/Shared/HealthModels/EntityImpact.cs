// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.HealthModels;

/// <summary>
/// Controls how much of a child entity's health state is propagated to its parents.
/// </summary>
/// <remarks>
/// Mirrors <c>EntityProperties.impact</c> in Azure Monitor health models. Impact is configured on the
/// <em>child</em> and is applied before the parent aggregates its dependencies, so impact and the parent's
/// <see cref="DependenciesAggregation"/> compose.
/// See https://learn.microsoft.com/azure/azure-monitor/health-models/concepts#impact-child.
/// </remarks>
public enum EntityImpact
{
    /// <summary>The child's state is propagated to the parent unchanged. This is the default.</summary>
    Standard,

    /// <summary>
    /// The child can never report worse than <see cref="HealthState.Degraded"/> to its parent.
    /// <see cref="HealthState.Degraded"/> is not propagated at all and <see cref="HealthState.Unhealthy"/>
    /// is propagated as <see cref="HealthState.Degraded"/>.
    /// </summary>
    Limited,

    /// <summary>The child never affects its parent. The parent always observes it as healthy.</summary>
    Suppressed
}
