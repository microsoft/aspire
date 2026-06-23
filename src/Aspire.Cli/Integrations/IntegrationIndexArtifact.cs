// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal sealed record IntegrationIndexArtifact
{
    public required int SchemaVersion { get; init; }

    public required IntegrationIndexArtifactDescriptor Index { get; init; }

    public required IReadOnlyList<IntegrationIndexArtifactEntry> Entries { get; init; }
}

internal sealed record IntegrationIndexArtifactDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Provenance { get; init; }
}

internal sealed record IntegrationIndexArtifactEntry
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public required IReadOnlyList<IntegrationIndexArtifactProvider> Providers { get; init; }
}

internal sealed record IntegrationIndexArtifactProvider
{
    public required string Type { get; init; }

    public required string Package { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];
}
