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
/// pseudo-terminal (PTY) per replica and a hidden <see cref="TerminalHostResource"/> per
/// replica bridges that replica's PTY traffic over Hex1b's HMP v1 protocol so that the
/// Aspire Dashboard and the <c>aspire terminal</c> CLI command can attach to live sessions.
/// </para>
/// <para>
/// A target resource with <c>N</c> replicas results in <c>N</c> entries in
/// <see cref="TerminalHosts"/>, indexed by parent replica index. The collection is built
/// at <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>
/// time from the resource's <see cref="ReplicaAnnotation"/>.
/// </para>
/// <para>
/// Connection direction across all UDS endpoints: the terminal host LISTENS; DCP, viewers,
/// and the AppHost DIAL. See <see cref="TerminalHostLayout"/> for the per-host path layout.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, ReplicaCount = {TerminalHosts.Count}")]
public sealed class TerminalAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalAnnotation"/> class.
    /// </summary>
    /// <param name="terminalHosts">The hidden per-replica terminal host resources that bridge PTY traffic for the annotated resource. Indexed by parent replica index.</param>
    /// <param name="options">The terminal options for this annotation.</param>
    public TerminalAnnotation(IReadOnlyList<TerminalHostResource> terminalHosts, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(terminalHosts);
        ArgumentNullException.ThrowIfNull(options);

        if (terminalHosts.Count == 0)
        {
            throw new ArgumentException("At least one terminal host is required.", nameof(terminalHosts));
        }

        for (var i = 0; i < terminalHosts.Count; i++)
        {
            if (terminalHosts[i] is null)
            {
                throw new ArgumentException($"Terminal host at index {i} is null.", nameof(terminalHosts));
            }
        }

        TerminalHosts = terminalHosts;
        Options = options;
    }

    /// <summary>
    /// Gets the hidden per-replica terminal host resources that bridge PTY traffic for
    /// the annotated resource. Indexed by parent replica index (0..N-1 where N is the
    /// parent's replica count at <c>WithTerminal()</c> time).
    /// </summary>
    public IReadOnlyList<TerminalHostResource> TerminalHosts { get; }

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
