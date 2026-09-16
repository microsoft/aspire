// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeAgentAssetSource : IAgentAssetSource
{
    private int _requestCount;

    public AgentAssetSourceResult Result { get; init; } = AgentAssetSourceResult.Unavailable("No assets configured.");

    public int RequestCount => Volatile.Read(ref _requestCount);

    public Task<AgentAssetSourceResult> GetAssetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(Result);
    }
}
