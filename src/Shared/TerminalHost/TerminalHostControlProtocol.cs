// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Wire-types exchanged over the terminal host's control UDS via StreamJsonRpc.
/// Shared between the AppHost (caller) and the Aspire.TerminalHost (callee).
/// </summary>
/// <remarks>
/// Each <c>aspire.terminalhost</c> process serves exactly one replica's session, so the
/// control protocol describes a single session per host. To enumerate all replicas of a
/// target resource, the AppHost iterates its per-replica hosts and queries each one's
/// control UDS independently.
/// </remarks>
internal static class TerminalHostControlProtocol
{
    /// <summary>
    /// Current control protocol version. Incremented on breaking changes.
    /// </summary>
    /// <remarks>
    /// Bumped to <c>2</c> when the protocol shifted from "one host with N replica sessions"
    /// to "one host per replica with a single session" (renamed <c>getReplicas</c> to
    /// <c>getSession</c> and dropped the replica count from <c>getInfo</c>).
    /// </remarks>
    public const int ProtocolVersion = 2;

    /// <summary>
    /// JSON-RPC method name for retrieving the host's single replica session state.
    /// Returns a <see cref="TerminalHostSessionInfo"/>.
    /// </summary>
    public const string GetSessionMethod = "getSession";

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
/// Information about the single replica session managed by one terminal host process.
/// </summary>
/// <remarks>
/// The host has no notion of its global replica index — that's encoded in the UDS paths
/// the AppHost passes in and is reattached by the AppHost when it aggregates per-host
/// state into the per-resource view shown by the Dashboard and <c>aspire terminal ps</c>.
/// </remarks>
internal sealed class TerminalHostSessionInfo
{
    /// <summary>
    /// Path to the producer-side UDS the host is LISTENING on. DCP dials this path to
    /// stream PTY traffic into the host. Echoed for diagnostics; the AppHost is the
    /// source of truth for the path layout.
    /// </summary>
    public required string ProducerUdsPath { get; init; }

    /// <summary>
    /// Path to the consumer-side UDS the host is LISTENING on. Viewers (Dashboard, CLI)
    /// dial this path to attach. Echoed for diagnostics.
    /// </summary>
    public required string ConsumerUdsPath { get; init; }

    /// <summary>
    /// True while the host's most recent <c>Hex1bTerminal</c> cycle has an attached
    /// upstream producer. Becomes false transiently between recycles (when DCP relaunches
    /// the underlying process), and permanently when the host is torn down.
    /// </summary>
    public required bool IsAlive { get; init; }

    /// <summary>
    /// Exit code from the most recently-completed <c>Hex1bTerminal</c> cycle, or null if
    /// no cycle has completed yet.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// True when the host's current cycle has an attached upstream producer. Identical
    /// in meaning to <see cref="IsAlive"/>; exposed under a clearer name for callers
    /// that want explicit "is the producer connected right now?" semantics.
    /// </summary>
    public bool ProducerConnected { get; init; }

    /// <summary>
    /// Number of completed <c>Hex1bTerminal</c> cycles. Increments each time the producer
    /// disconnects and the host rebinds. Zero on first cycle.
    /// </summary>
    public int RestartCount { get; init; }

    /// <summary>
    /// Current terminal grid width in columns, as last negotiated by the active HMP1
    /// primary peer. Falls back to the AppHost-configured initial width when no peer has
    /// driven a resize. Optional: nullable so older clients deserialize cleanly.
    /// </summary>
    public int? CurrentColumns { get; init; }

    /// <summary>
    /// Current terminal grid height in rows. See <see cref="CurrentColumns"/>.
    /// </summary>
    public int? CurrentRows { get; init; }

    /// <summary>
    /// Number of HMP1 peers currently connected to the consumer UDS. Optional for
    /// back-compat with older hosts.
    /// </summary>
    public int? AttachedPeerCount { get; init; }

    /// <summary>
    /// Per-peer identification for currently-connected HMP1 viewers, in connect order.
    /// Optional for back-compat with older hosts.
    /// </summary>
    public TerminalHostPeerInfo[]? Peers { get; init; }
}

/// <summary>
/// Per-peer identification for an HMP1 client currently connected to a host's consumer
/// UDS. The HMP1 server assigns the <see cref="PeerId"/> at handshake; the
/// <see cref="DisplayName"/> is whatever the client passed in its ClientHello (e.g.
/// <c>aspire-cli:1234</c> or <c>dashboard:abc12345</c>).
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
/// Response from <see cref="TerminalHostControlProtocol.GetInfoMethod"/>.
/// </summary>
internal sealed class TerminalHostInfoResponse
{
    /// <summary>
    /// Control protocol version. See <see cref="TerminalHostControlProtocol.ProtocolVersion"/>.
    /// </summary>
    public required int ProtocolVersion { get; init; }
}
