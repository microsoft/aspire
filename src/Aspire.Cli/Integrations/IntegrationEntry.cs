// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal sealed record IntegrationEntry
{
    public required IntegrationIndexDescriptor Index { get; init; }

    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public required IReadOnlyList<IntegrationProviderReference> Providers { get; init; }
}
