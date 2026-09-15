// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.ResourceGraph;

public sealed class ResourceDto
{
    public required string Name { get; init; }
    public required string ResourceType { get; init; }
    public required string DisplayName { get; init; }
    public required string Uid { get; init; }
    public required IconDto ResourceIcon { get; init; }
    public required IconDto StateIcon { get; init; }
    public required string? EndpointUrl { get; init; }
    public required string? EndpointText { get; init; }

    /// <summary>
    /// Whether this is the synthetic AppHost root rather than a resource with executable commands.
    /// </summary>
    public bool IsAppHost { get; init; }

    /// <summary>
    /// The names of the resources this resource depends on. Each becomes a parent-to-child link in the graph.
    /// </summary>
    public required ImmutableArray<string> ChildNames { get; init; }

    /// <summary>
    /// The health state to display for this resource, after the health of everything it depends on has been
    /// rolled up into it. Used to colour the node and the links to its children.
    /// </summary>
    /// <remarks>
    /// Serialized as the enum name because the value is only used by the graph script to pick a CSS class.
    /// </remarks>
    public required string HealthState { get; init; }
}
