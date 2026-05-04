// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A hidden, DCP-launched executable resource that bridges per-replica PTY traffic between
/// DCP (the producer) and viewers like the Aspire Dashboard and the <c>aspire terminal</c>
/// CLI command (the consumers) using Hex1b's HMP v1 muxer.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="TerminalHostResource"/> instance is created for each target resource that
/// is configured with <see cref="TerminalResourceBuilderExtensions.WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>.
/// </para>
/// <para>
/// Because <see cref="TerminalHostResource"/> derives from <see cref="ExecutableResource"/>, DCP
/// launches it like any other executable, which keeps the host process out-of-process and
/// isolates PTY state from the AppHost. The actual binary path is resolved during
/// <see cref="Aspire.Hosting.ApplicationModel.BeforeStartEvent"/> from <see cref="Aspire.Hosting.Dcp.DcpOptions.TerminalHostPath"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, Parent = {Parent.Name}, ReplicaCount = {Layout.ReplicaCount}")]
public sealed class TerminalHostResource : ExecutableResource, IResourceWithParent<IResource>
{
    // The host is created before its real binary path is known (DcpOptions is not yet
    // configured at WithTerminal time). A BeforeStartEvent subscriber rewrites the
    // ExecutableAnnotation.Command before DCP launches the resource. This sentinel makes
    // misconfiguration easy to spot if the BeforeStart hook is somehow skipped.
    internal const string UnresolvedCommand = "<unresolved-aspire-terminalhost>";

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalHostResource"/> class.
    /// </summary>
    /// <param name="name">The name of the terminal host resource.</param>
    /// <param name="parent">The target resource that this terminal host serves.</param>
    /// <param name="layout">The Unix domain socket layout this host will own.</param>
    public TerminalHostResource(string name, IResource parent, TerminalHostLayout layout)
        : base(name, UnresolvedCommand, layout?.BaseDirectory ?? string.Empty)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(layout);

        Parent = parent;
        Layout = layout;
    }

    /// <summary>
    /// Gets the target resource that this terminal host serves.
    /// </summary>
    public IResource Parent { get; }

    /// <summary>
    /// Gets the Unix domain socket layout this host owns.
    /// </summary>
    public TerminalHostLayout Layout { get; }
}
