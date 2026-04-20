// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A hidden resource that manages the terminal host process for a parent resource.
/// </summary>
/// <remarks>
/// The terminal host process uses Hex1b to maintain a headless terminal state and serves
/// as the bridge between DCP's PTY forwarding and clients (Dashboard, CLI) that connect
/// via Unix domain socket. One terminal host is created per resource that has
/// <see cref="TerminalAnnotation"/> applied.
/// </remarks>
internal sealed class TerminalHostResource : Resource, IResourceWithParent<IResource>, IResourceWithWaitSupport
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalHostResource"/> class.
    /// </summary>
    /// <param name="name">The name of the terminal host resource.</param>
    /// <param name="parent">The resource that this terminal host serves.</param>
    public TerminalHostResource(string name, IResource parent)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;
    }

    /// <summary>
    /// Gets the parent resource that this terminal host serves.
    /// </summary>
    public IResource Parent { get; }
}
