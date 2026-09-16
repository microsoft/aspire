// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Resolves installable assets without exposing source-specific packages, formats or caches.
/// </summary>
internal interface IAgentAssetSource
{
    Task<AgentAssetSourceResult> GetAssetsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Contains resolved assets or the diagnostic explaining why their source is unavailable.
/// </summary>
internal sealed class AgentAssetSourceResult
{
    private AgentAssetSourceResult(bool isAvailable, IReadOnlyList<AgentAssetDefinition> assets, string? message)
    {
        IsAvailable = isAvailable;
        Assets = [.. assets];
        Message = message;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<AgentAssetDefinition> Assets { get; }

    public string? Message { get; }

    public static AgentAssetSourceResult Available(IReadOnlyList<AgentAssetDefinition> assets)
        => new(true, assets, message: null);

    public static AgentAssetSourceResult Unavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(false, [], message);
    }
}
