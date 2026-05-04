// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource as having an interactive terminal session.
/// </summary>
/// <remarks>
/// <para>
/// When this annotation is present on a resource, the orchestrator (DCP) allocates a
/// pseudo-terminal (PTY) per replica and a hidden <see cref="TerminalHostResource"/> bridges
/// the PTY traffic over Hex1b's HMP v1 protocol so that the Aspire Dashboard and the
/// <c>aspire terminal</c> CLI command can attach to live sessions.
/// </para>
/// <para>
/// The set of Unix domain socket paths used to wire DCP, the host, and viewers together is
/// described by <see cref="TerminalHostResource.Layout"/> on the resource referenced by
/// <see cref="TerminalHost"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, TerminalHost = {TerminalHost.Name}")]
public sealed class TerminalAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalAnnotation"/> class.
    /// </summary>
    /// <param name="terminalHost">The hidden terminal host resource that bridges PTY traffic for the annotated resource.</param>
    /// <param name="options">The terminal options for this annotation.</param>
    public TerminalAnnotation(TerminalHostResource terminalHost, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(terminalHost);
        ArgumentNullException.ThrowIfNull(options);

        TerminalHost = terminalHost;
        Options = options;
    }

    /// <summary>
    /// Gets the hidden terminal host resource that bridges PTY traffic for the annotated resource.
    /// </summary>
    public TerminalHostResource TerminalHost { get; }

    /// <summary>
    /// Gets the terminal options for this annotation.
    /// </summary>
    public TerminalOptions Options { get; }
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
