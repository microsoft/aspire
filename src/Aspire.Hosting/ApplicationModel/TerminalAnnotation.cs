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
/// The per-replica <see cref="TerminalHostResource"/>s are NOT created at
/// <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>
/// time. They are created during <see cref="BeforeStartEvent"/> by reading the parent
/// resource's final <see cref="ReplicaAnnotation"/>. This deferral is what makes
/// <c>WithReplicas(N)</c> work correctly even when it is called <strong>after</strong>
/// <c>WithTerminal()</c>: the model is fully built by the time the per-replica hosts are
/// materialized, so the final replica count is always honoured. Until that initialization
/// runs, <see cref="TerminalHosts"/> is an empty collection and <see cref="IsInitialized"/>
/// is <c>false</c>.
/// </para>
/// <para>
/// Connection direction across all UDS endpoints: the terminal host LISTENS; DCP, viewers,
/// and the AppHost DIAL. See <see cref="TerminalHostLayout"/> for the per-host path layout.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, IsInitialized = {IsInitialized}, ReplicaCount = {TerminalHosts.Count}")]
public sealed class TerminalAnnotation : IResourceAnnotation
{
    // Starts as Array.Empty<TerminalHostResource>() (the default for [] in C#) so the
    // public TerminalHosts surface is always non-null and safely enumerable, even before
    // BeforeStartEvent has had a chance to materialize the per-replica hosts. Consumers
    // (DCP creators, dashboard data, backchannel) must guard with a Count check or an
    // index-bounds check; all of them already do.
    private IReadOnlyList<TerminalHostResource> _terminalHosts = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalAnnotation"/> class. The
    /// per-replica <see cref="TerminalHostResource"/>s are filled in later via
    /// <see cref="Initialize"/> from a <see cref="BeforeStartEvent"/> handler so the
    /// final <see cref="ReplicaAnnotation"/> count is always honoured.
    /// </summary>
    /// <param name="options">The terminal options for this annotation.</param>
    public TerminalAnnotation(TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
    }

    /// <summary>
    /// Gets the terminal options for this annotation.
    /// </summary>
    public TerminalOptions Options { get; }

    /// <summary>
    /// Gets the hidden per-replica terminal host resources that bridge PTY traffic for
    /// the annotated resource. Indexed by parent replica index (0..N-1 where N is the
    /// parent's replica count at <see cref="BeforeStartEvent"/> time). Empty until
    /// <see cref="Initialize"/> has been called.
    /// </summary>
    public IReadOnlyList<TerminalHostResource> TerminalHosts => _terminalHosts;

    /// <summary>
    /// Gets a value indicating whether <see cref="Initialize"/> has been called yet.
    /// Production code initializes during <see cref="BeforeStartEvent"/>; tests that
    /// inspect <see cref="TerminalHosts"/> need to publish that event manually first.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Gets the per-target temporary base directory under which every per-replica host's
    /// UDS triple lives (each at <c>{BaseDirectory}/{i}/</c>). Shared across all hosts of
    /// the same parent so cleanup is a single recursive delete. <c>null</c> until
    /// <see cref="Initialize"/> has been called.
    /// </summary>
    public string? BaseDirectory { get; private set; }

    /// <summary>
    /// Populates <see cref="TerminalHosts"/> and <see cref="BaseDirectory"/> exactly once.
    /// Called by the <see cref="BeforeStartEvent"/> subscriber installed by
    /// <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when called more than once.</exception>
    internal void Initialize(string baseDirectory, IReadOnlyList<TerminalHostResource> terminalHosts)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentNullException.ThrowIfNull(terminalHosts);

        if (IsInitialized)
        {
            throw new InvalidOperationException("TerminalAnnotation has already been initialized.");
        }

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

        BaseDirectory = baseDirectory;
        _terminalHosts = terminalHosts;
        IsInitialized = true;
    }
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
