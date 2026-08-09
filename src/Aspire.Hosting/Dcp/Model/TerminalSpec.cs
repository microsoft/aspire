// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

/// <summary>
/// Terminal configuration for a DCP resource. When set, DCP allocates a pseudo-terminal
/// for the process and either listens on or dials the per-replica HMP v1 endpoint at
/// <see cref="UdsPath"/>, depending on <see cref="SocketMode"/>.
/// </summary>
/// <remarks>
/// Aspire's terminal host owns the listener (each terminal host process binds the UDS path
/// before DCP creates the resource), so we always set <see cref="SocketMode"/> to
/// <c>"connect"</c>. The HMP v1 data flow is identical in both modes — only connection
/// establishment differs. Mirrors <c>api/v1/terminal_types.go</c> in microsoft/dcp; field
/// names and JSON tags must stay in lockstep with the Go side.
/// </remarks>
internal sealed class TerminalSpec
{
    /// <summary>
    /// Path to the Unix domain socket used for the HMP v1 connection between DCP and the
    /// Aspire terminal host. Required.
    /// </summary>
    [JsonPropertyName("udsPath")]
    public string? UdsPath { get; set; }

    /// <summary>
    /// Selects how DCP establishes the HMP v1 connection over <see cref="UdsPath"/>.
    /// <c>"listen"</c> (the DCP default) means DCP listens on the socket and the client
    /// connects to it; <c>"connect"</c> means the client (the Aspire terminal host) listens
    /// on the socket and DCP dials it. Aspire always sets this to <c>"connect"</c>.
    /// </summary>
    [JsonPropertyName("socketMode")]
    public string? SocketMode { get; set; }

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
