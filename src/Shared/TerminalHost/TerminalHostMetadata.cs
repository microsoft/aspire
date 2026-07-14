// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Schema for the <c>{replicaId}.metadata.json</c> sidecar that the AppHost writes next
/// to each replica's UDS sockets under <c>~/.aspire/trmnl/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written once by <c>TerminalResourceBuilderExtensions.MaterializeTerminalHosts</c> when
/// the AppHost materializes each <c>TerminalHostResource</c>
/// (during <c>BeforeStartEvent</c>) and deleted on <c>ApplicationStopped</c> alongside the
/// <c>.sock</c> files. The descriptor lets external tools enumerate live terminals by
/// listing <c>~/.aspire/trmnl/*.metadata.json</c> without needing an active backchannel.
/// </para>
/// <para>
/// All fields capture state known at AppHost startup; runtime-mutable state (current
/// dimensions after a downstream resize, attached peer count) is intentionally NOT
/// persisted here — that lives inside the terminal-host process and is reachable via the
/// control UDS. The on-disk file is read-mostly.
/// </para>
/// </remarks>
internal sealed class TerminalHostMetadata
{
    /// <summary>
    /// Bumped when fields are added or semantics change so older readers can refuse
    /// unknown schemas instead of silently misinterpreting them.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The replica id (see <see cref="TerminalHostPaths.ComputeReplicaId(string, string, int)"/>).</summary>
    [JsonPropertyName("replicaId")]
    public required string ReplicaId { get; init; }

    /// <summary>Name of the parent Aspire resource this terminal host serves.</summary>
    [JsonPropertyName("resourceName")]
    public required string ResourceName { get; init; }

    /// <summary>Zero-based replica index within <see cref="ResourceName"/>.</summary>
    [JsonPropertyName("replicaIndex")]
    public required int ReplicaIndex { get; init; }

    /// <summary>Absolute path to the AppHost project that owns this terminal host.</summary>
    [JsonPropertyName("appHostPath")]
    public required string AppHostPath { get; init; }

    /// <summary>
    /// Process id of the AppHost process. Stale sidecars (whose PID no longer exists)
    /// can be safely garbage-collected by external tools.
    /// </summary>
    [JsonPropertyName("appHostPid")]
    public required int AppHostPid { get; init; }

    /// <summary>UTC timestamp when the sidecar was written.</summary>
    [JsonPropertyName("createdAtUtc")]
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Initial terminal width in columns (as configured by <c>WithTerminal(...)</c>).</summary>
    [JsonPropertyName("columns")]
    public required int Columns { get; init; }

    /// <summary>Initial terminal height in rows.</summary>
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    /// <summary>Path of the control UDS. Convenience for tools so they don't have to recompute it.</summary>
    [JsonPropertyName("controlSocketPath")]
    public required string ControlSocketPath { get; init; }

    /// <summary>Path of the consumer (viewer-facing) UDS.</summary>
    [JsonPropertyName("consumerSocketPath")]
    public required string ConsumerSocketPath { get; init; }
}
