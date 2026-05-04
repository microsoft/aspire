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
    /// True when the replica's relay terminal is still running.
    /// False once the upstream PTY has exited or the relay was torn down.
    /// </summary>
    public required bool IsAlive { get; init; }

    /// <summary>
    /// Exit code of the upstream process if the replica has terminated.
    /// Null while the replica is still alive.
    /// </summary>
    public int? ExitCode { get; init; }
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
