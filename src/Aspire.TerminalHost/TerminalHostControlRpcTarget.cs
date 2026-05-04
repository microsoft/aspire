// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;

namespace Aspire.TerminalHost;

/// <summary>
/// StreamJsonRpc target exposed over the terminal host's control UDS. Handles
/// status queries and shutdown requests from the AppHost.
/// </summary>
internal sealed class TerminalHostControlRpcTarget
{
    private readonly TerminalHostApp _app;

    public TerminalHostControlRpcTarget(TerminalHostApp app)
    {
        _app = app;
    }

    /// <summary>
    /// Returns the current set of replica sessions and their liveness state.
    /// </summary>
    public Task<TerminalHostReplicasResponse> GetReplicasAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var replicas = _app.SnapshotReplicas();
        return Task.FromResult(new TerminalHostReplicasResponse { Replicas = replicas });
    }

    /// <summary>
    /// Returns the host's protocol version and replica count. Useful as a
    /// fast liveness probe and to negotiate future protocol upgrades.
    /// </summary>
    public Task<TerminalHostInfoResponse> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(new TerminalHostInfoResponse
        {
            ProtocolVersion = TerminalHostControlProtocol.ProtocolVersion,
            ReplicaCount = _app.ReplicaCount,
        });
    }

    /// <summary>
    /// Requests a clean shutdown of the terminal host. The host will tear down
    /// each replica relay and exit shortly after this call returns.
    /// </summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _app.RequestShutdown();
        return Task.CompletedTask;
    }
}
