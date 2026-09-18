// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.HealthModels;

/// <summary>
/// How an entity aggregates the health of its child entities into a single state.
/// </summary>
/// <remarks>
/// Mirrors <c>signalGroups.dependencies</c> in Azure Monitor health models, which is configured on the
/// <em>parent</em> entity. See https://learn.microsoft.com/azure/azure-monitor/health-models/rollup.
/// </remarks>
public sealed record DependenciesAggregation
{
    /// <summary>The default rollup, where the worst state across all children wins.</summary>
    public static DependenciesAggregation WorstOf { get; } = new();

    /// <summary>The strategy used to combine child health states.</summary>
    public DependenciesAggregationType AggregationType { get; init; } = DependenciesAggregationType.WorstOf;

    /// <summary>
    /// The threshold at which the entity becomes <see cref="HealthState.Degraded"/>.
    /// Only meaningful for the threshold-based aggregation types. When it is not set the entity moves
    /// straight from <see cref="HealthState.Healthy"/> to <see cref="HealthState.Unhealthy"/> with no
    /// intermediate degraded state, which matches the Azure behaviour when <c>degradedThreshold</c> is omitted.
    /// </summary>
    public double? DegradedThreshold { get; init; }

    /// <summary>
    /// The threshold at which the entity becomes <see cref="HealthState.Unhealthy"/>.
    /// Required by the threshold-based aggregation types and must not be set for
    /// <see cref="DependenciesAggregationType.WorstOf"/>.
    /// </summary>
    public double? UnhealthyThreshold { get; init; }

    /// <summary>Whether thresholds are counts of children or a percentage of them.</summary>
    public AggregationUnit Unit { get; init; } = AggregationUnit.Absolute;

    /// <summary>
    /// Whether children in <see cref="HealthState.Unknown"/> are excluded from the threshold calculation.
    /// Defaults to <see langword="true"/> to match the Azure default (the portal's "Ignore unknown" checkbox
    /// is selected by default).
    /// </summary>
    public bool IgnoreUnknown { get; init; } = true;
}

/// <summary>
/// The strategy an entity uses to combine the health states of its children.
/// </summary>
/// <remarks>
/// The lite editor supports this subset of the <c>2026-09-01-preview</c> Azure API's aggregation types.
/// <c>BestOf</c> is intentionally outside the initial editor's supported policies.
/// </remarks>
public enum DependenciesAggregationType
{
    /// <summary>The worst (most severe) child state becomes the parent state.</summary>
    WorstOf,

    /// <summary>
    /// The parent degrades when the number or percentage of <em>healthy</em> children falls to or below
    /// the threshold. Thresholds read as "at least this many children must be healthy".
    /// </summary>
    MinHealthy,

    /// <summary>
    /// The parent degrades when the number or percentage of <em>not healthy</em> children reaches or
    /// exceeds the threshold. Thresholds read as "no more than this many children may be unhealthy".
    /// </summary>
    MaxNotHealthy
}

/// <summary>
/// Whether an aggregation threshold is an absolute count of entities or a percentage of them.
/// </summary>
public enum AggregationUnit
{
    /// <summary>The threshold is a count of child entities.</summary>
    Absolute,

    /// <summary>The threshold is a percentage between 0 and 100.</summary>
    Percentage
}
