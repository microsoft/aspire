// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

/// <summary>
/// Terminal configuration for a DCP resource. When set, DCP allocates a pseudo-terminal
/// for the process and serves the per-replica HMP v1 producer endpoint at <see cref="UdsPath"/>.
/// The Aspire terminal host connects to that endpoint as an HMP v1 client.
/// </summary>
/// <remarks>
/// Mirrors <c>api/v1/terminal_types.go</c> in microsoft/dcp; field names and JSON tags must
/// stay in lockstep with the Go side.
/// </remarks>
internal sealed class TerminalSpec
{
    /// <summary>
    /// Whether terminal (PTY) mode is enabled for this resource. When true, DCP allocates
    /// a pseudo-terminal instead of pipes for the process I/O and exposes an HMP v1
    /// producer endpoint at <see cref="UdsPath"/>.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the Unix domain socket that DCP listens on for the terminal host's HMP v1
    /// client connection. Required when <see cref="Enabled"/> is true.
    /// </summary>
    [JsonPropertyName("udsPath")]
    public string? UdsPath { get; set; }

    /// <summary>
    /// Initial terminal width in columns. When <c>0</c>, DCP applies a default of 80.
    /// </summary>
    [JsonPropertyName("cols")]
    public int Cols { get; set; }

    /// <summary>
    /// Initial terminal height in rows. When <c>0</c>, DCP applies a default of 24.
    /// </summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; }
}
