// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

/// <summary>
/// Terminal configuration for a DCP resource. When present, DCP allocates a pseudo-terminal
/// and forwards I/O over a Unix domain socket using the Aspire Terminal Protocol.
/// </summary>
internal sealed class TerminalSpec
{
    /// <summary>
    /// Whether terminal (PTY) mode is enabled for this resource.
    /// When true, DCP allocates a pseudo-terminal instead of pipes for the process I/O.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the Unix domain socket where DCP will serve the Aspire Terminal Protocol.
    /// If not specified, DCP will generate a path and report it in the resource status.
    /// </summary>
    [JsonPropertyName("socketPath")]
    public string? SocketPath { get; set; }

    /// <summary>
    /// Initial terminal width in columns.
    /// </summary>
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 120;

    /// <summary>
    /// Initial terminal height in rows.
    /// </summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 30;
}
