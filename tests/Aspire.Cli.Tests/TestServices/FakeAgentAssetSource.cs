// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeAgentAssetSource : IAgentAssetSource
{
    private readonly ConcurrentQueue<AgentAssetKind> _requestedKinds = new();

    public AgentAssetSourceResult Skills { get; init; } = AgentAssetSourceResult.Unavailable("No skills configured.");

    public AgentAssetSourceResult Extensions { get; init; } = AgentAssetSourceResult.Unavailable("No extensions configured.");

    public IReadOnlyList<AgentAssetKind> RequestedKinds => _requestedKinds.ToArray();

    public Task<AgentAssetSourceResult> GetAssetsAsync(AgentAssetKind assetKind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requestedKinds.Enqueue(assetKind);
        return Task.FromResult(assetKind switch
        {
            AgentAssetKind.Skill => Skills,
            AgentAssetKind.Extension => Extensions,
            _ => throw new ArgumentOutOfRangeException(nameof(assetKind), assetKind, null)
        });
    }
}
