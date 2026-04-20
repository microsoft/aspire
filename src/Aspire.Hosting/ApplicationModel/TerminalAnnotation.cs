// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents terminal configuration for a resource that supports interactive terminal sessions.
/// </summary>
/// <remarks>
/// When this annotation is present on a resource, the orchestrator will allocate a pseudo-terminal (PTY)
/// for the resource's process and make it available for interactive access via a Unix domain socket.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, SocketPath = {SocketPath}")]
public sealed class TerminalAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the path to the Unix domain socket used for terminal I/O.
    /// </summary>
    /// <remarks>
    /// This path is set by the orchestrator when the terminal host is started,
    /// or resolved from <see cref="SocketPathProvider"/> for custom terminal resources.
    /// Clients connect to this socket to interact with the resource's terminal session.
    /// </remarks>
    public string? SocketPath { get; set; }

    /// <summary>
    /// Gets or sets an optional callback that provides the UDS socket path.
    /// </summary>
    /// <remarks>
    /// Used by custom terminal resources that manage their own terminal server.
    /// When set, the orchestrator calls this during resource initialization to obtain the socket path
    /// instead of creating a hidden terminal host resource.
    /// </remarks>
    public Func<CancellationToken, Task<string>>? SocketPathProvider { get; set; }

    /// <summary>
    /// Gets the terminal options for this annotation.
    /// </summary>
    public TerminalOptions Options { get; init; } = new();
}

/// <summary>
/// Options for configuring a terminal session.
/// </summary>
public sealed class TerminalOptions
{
    /// <summary>
    /// Gets or sets the initial number of columns for the terminal. Defaults to 120.
    /// </summary>
    public int Columns { get; set; } = 120;

    /// <summary>
    /// Gets or sets the initial number of rows for the terminal. Defaults to 30.
    /// </summary>
    public int Rows { get; set; } = 30;

    /// <summary>
    /// Gets or sets the shell to use for the terminal session.
    /// </summary>
    /// <remarks>
    /// When <c>null</c>, the default shell for the resource is used.
    /// For containers, this is typically <c>/bin/sh</c>. For executables, the process itself serves as the terminal program.
    /// </remarks>
    public string? Shell { get; set; }
}
