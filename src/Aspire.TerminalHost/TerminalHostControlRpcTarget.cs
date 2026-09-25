// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;

namespace Aspire.TerminalHost;

/// <summary>
/// StreamJsonRpc target exposed over the terminal host's control UDS. Handles
/// status queries and shutdown requests from the AppHost.
/// </summary>
/// <remarks>
/// Each terminal host process serves a single replica, so this target exposes a
/// single-session view rather than a list. The AppHost iterates per-replica hosts
/// and aggregates their responses to build cross-resource state.
/// </remarks>
internal sealed class TerminalHostControlRpcTarget
{
    private readonly TerminalHostApp _app;

    public TerminalHostControlRpcTarget(TerminalHostApp app)
    {
        _app = app;
    }

    /// <summary>
    /// Returns the host's single replica session and its current liveness state.
    /// </summary>
    public Task<TerminalHostSessionInfo> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_app.SnapshotSession());
    }

    /// <summary>
    /// Returns the host's protocol version. Useful as a fast liveness probe and to
    /// negotiate future protocol upgrades.
    /// </summary>
    /// <remarks>
    /// Kept as an instance method (not static) so that StreamJsonRpc's
    /// <c>AddLocalRpcTarget(this)</c> enumeration discovers it alongside the other
    /// session-bound methods. CA1822 is suppressed for this reason.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Must remain an instance method so StreamJsonRpc.AddLocalRpcTarget(this) registers it as an RPC method.")]
    public Task<TerminalHostInfoResponse> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(new TerminalHostInfoResponse
        {
            ProtocolVersion = TerminalHostControlProtocol.ProtocolVersion,
        });
    }

    /// <summary>
    /// Requests a clean shutdown of the terminal host. The host will tear down
    /// its replica relay and exit shortly after this call returns.
    /// </summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _app.RequestShutdown();
        return Task.CompletedTask;
    }
}
