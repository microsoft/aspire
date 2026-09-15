// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Aspire.HealthModels;

/// <summary>Coordinates in the model's canvas space, independent of zoom and viewport size.</summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public sealed record HealthModelCanvasPosition(double X, double Y);

/// <summary>A local signal binding, without measurements, descriptions, or exception data.</summary>
/// <param name="Name">The local signal name, unique within its entity.</param>
/// <param name="Kind">The signal's data source.</param>
public sealed record HealthModelSignalBinding(string Name, SignalKind Kind);

/// <summary>Portable configuration for an entity; observed health is deliberately excluded.</summary>
public sealed record HealthModelEntityConfiguration
{
    /// <summary>Gets the entity identifier used by relationships.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the name shown in the designer.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the bound AppHost resource name, without the runtime-generated instance suffix.</summary>
    public string? AspireResourceName { get; init; }

    /// <summary>Gets the replica index of the bound AppHost resource.</summary>
    public int? ReplicaIndex { get; init; }

    /// <summary>Gets the saved position in the model's canvas space.</summary>
    public required HealthModelCanvasPosition CanvasPosition { get; init; }

    /// <summary>Gets how much of this entity's health is propagated to its parents.</summary>
    public EntityImpact Impact { get; init; }

    /// <summary>Gets the target percentage of time this entity is expected to be healthy.</summary>
    public double? HealthObjective { get; init; }

    /// <summary>Gets how this entity combines the health of its children.</summary>
    public DependenciesAggregation Dependencies { get; init; } = DependenciesAggregation.WorstOf;

    /// <summary>Gets the local signal bindings, without observed state or telemetry.</summary>
    public ImmutableArray<HealthModelSignalBinding> LocalSignals { get; init; } = [];
}

/// <summary>
/// A directed parent-to-child edge in a health model.
/// </summary>
/// <remarks>
/// Azure models relationships as standalone resources with immutable <c>parentEntityName</c> and
/// <c>childEntityName</c>, and carries no health or aggregation configuration on the edge itself. Rollup
/// tuning lives on the two entities instead: <see cref="HealthModelEntityConfiguration.Impact"/> on the child
/// and <see cref="HealthModelEntityConfiguration.Dependencies"/> on the parent.
/// </remarks>
/// <param name="ParentEntityName">The name of the parent entity.</param>
/// <param name="ChildEntityName">The name of the child entity.</param>
public sealed record HealthModelRelationship(string ParentEntityName, string ChildEntityName);

/// <summary>A versioned, portable model definition for saved layout and publishing integration.</summary>
/// <remarks>
/// This is not an ARM template. Entity names, relationships, impact, dependency settings and canvas
/// coordinates map to Microsoft.CloudHealth entities. Local signal bindings still require an Azure
/// metric/query mapping or an external signal producer when a publisher consumes this definition.
/// </remarks>
public sealed record HealthModelDocument
{
    /// <summary>Gets the portable schema version, which must be explicitly present in JSON.</summary>
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the model identifier, which is also the root entity's identifier.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the application name used to associate a saved model with its AppHost.</summary>
    public required string ApplicationName { get; init; }

    /// <summary>Gets all entities, including the root, in their saved order.</summary>
    public required ImmutableArray<HealthModelEntityConfiguration> Entities { get; init; }

    /// <summary>Gets the parent-to-child edges in their saved order.</summary>
    public required ImmutableArray<HealthModelRelationship> Relationships { get; init; }
}
