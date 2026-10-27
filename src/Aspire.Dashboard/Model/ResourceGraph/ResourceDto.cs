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
    public required ImmutableArray<string> ReferencedNames { get; init; }
    public required bool IsHidden { get; init; }
}
