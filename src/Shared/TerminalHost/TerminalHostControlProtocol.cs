// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Wire-types exchanged over the terminal host's control UDS via StreamJsonRpc.
/// Shared between the AppHost (caller) and the Aspire.TerminalHost (callee).
/// </summary>
internal static class TerminalHostControlProtocol
{
    /// <summary>
    /// Current control protocol version. Incremented on breaking changes.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// JSON-RPC method name for retrieving the current set of replica sessions.
    /// </summary>
    public const string GetReplicasMethod = "getReplicas";

    /// <summary>
    /// JSON-RPC method name for requesting a clean shutdown of the terminal host.
    /// </summary>
    public const string ShutdownMethod = "shutdown";

    /// <summary>
    /// JSON-RPC method name for retrieving the protocol/host version.
    /// </summary>
    public const string GetInfoMethod = "getInfo";
}

/// <summary>
/// Information about a single replica session managed by the terminal host.
/// </summary>
internal sealed class TerminalHostReplicaInfo
{
    /// <summary>
    /// Zero-based replica index. Stable across the lifetime of the host.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Path to the producer-side UDS the host is consuming from (DCP listens on this).
    /// </summary>
    public required string ProducerUdsPath { get; init; }

    /// <summary>
    /// Path to the consumer-side UDS the host is serving on. Viewers (Dashboard, CLI)
    /// connect here.
    /// </summary>
    public required string ConsumerUdsPath { get; init; }

    /// <summary>
    /// True while the replica's most recent <c>Hex1bTerminal</c> cycle has
    /// an attached upstream producer. Becomes false transiently between recycles
    /// (when DCP relaunches the underlying process), and permanently when the
    /// replica is torn down with the host.
    ///
    /// Historically this meant "the replica is permanently terminated" — that
    /// has not been a useful signal since the host gained recycle support, so
    /// we now report transient connectivity. Callers that previously branched
    /// on <c>!IsAlive</c> to mean "show exit info" should treat a transient
    /// false as "currently waiting for the producer to come back".
    /// </summary>
    public required bool IsAlive { get; init; }

    /// <summary>
    /// Exit code from the most recently-completed <c>Hex1bTerminal</c>
    /// cycle, or null if no cycle has completed yet.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// True when the replica's current cycle has an attached upstream producer.
    /// Identical in meaning to <see cref="IsAlive"/>; exposed under a clearer name
    /// for callers that want explicit "is the producer connected right now?"
    /// semantics. Both fields are populated for backwards compatibility.
    /// </summary>
    public bool ProducerConnected { get; init; }

    /// <summary>
    /// Number of completed <c>Hex1bTerminal</c> cycles for this replica.
    /// Increments each time the producer disconnects and the replica rebinds.
    /// Zero on first cycle. Useful as a diagnostic signal — e.g. an unexpectedly
    /// high count indicates the upstream process is crashing repeatedly.
    /// </summary>
    public int RestartCount { get; init; }

    /// <summary>
    /// Current terminal grid width in columns, as last negotiated by the active
    /// HMP1 primary peer. Falls back to the AppHost-configured initial width
    /// (<see cref="TerminalHostInfoResponse"/>) when no peer has driven a resize.
    /// Optional: nullable so older hosts predating the terminal-ps metadata
    /// surface deserialize cleanly without throwing.
    /// </summary>
    public int? CurrentColumns { get; init; }

    /// <summary>
    /// Current terminal grid height in rows. See <see cref="CurrentColumns"/>.
    /// </summary>
    public int? CurrentRows { get; init; }

    /// <summary>
    /// Number of HMP1 peers (CLI <c>aspire terminal attach</c> sessions, browser
    /// dashboard tabs) currently connected to this replica's consumer UDS. Zero
    /// when no viewer is attached. Updated on each peer connect/disconnect.
    /// Optional for back-compat with older hosts.
    /// </summary>
    public int? AttachedPeerCount { get; init; }

    /// <summary>
    /// Per-peer identification for currently-connected HMP1 viewers, in connect order.
    /// Useful for diagnostics ("who currently holds the terminal?") in
    /// <c>aspire terminal ps</c>. Optional for back-compat with older hosts.
    /// </summary>
    public TerminalHostPeerInfo[]? Peers { get; init; }
}

/// <summary>
/// Per-peer identification for an HMP1 client currently connected to a replica's
/// consumer UDS. The HMP1 server assigns the <see cref="PeerId"/> at handshake;
/// the <see cref="DisplayName"/> is whatever the client passed in its ClientHello
/// (e.g. <c>aspire-cli:1234</c> or <c>dashboard:abc12345</c>).
/// </summary>
internal sealed class TerminalHostPeerInfo
{
    /// <summary>
    /// HMP1-assigned stable peer identifier for the lifetime of the connection.
    /// </summary>
    public required string PeerId { get; init; }

    /// <summary>
    /// Free-form label the peer reported in its ClientHello, or null if the
    /// peer didn't supply one.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Response from <see cref="TerminalHostControlProtocol.GetReplicasMethod"/>.
/// </summary>
internal sealed class TerminalHostReplicasResponse
{
    public required TerminalHostReplicaInfo[] Replicas { get; init; }
}

/// <summary>
/// Response from <see cref="TerminalHostControlProtocol.GetInfoMethod"/>.
/// </summary>
internal sealed class TerminalHostInfoResponse
{
    /// <summary>
    /// Control protocol version. See <see cref="TerminalHostControlProtocol.ProtocolVersion"/>.
    /// </summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>
    /// Number of replicas the host was started with.
    /// </summary>
    public required int ReplicaCount { get; init; }
}
