// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the Unix domain socket layout used by a single <see cref="TerminalHostResource"/>
/// to bridge one parent-resource replica's PTY traffic between DCP and viewers (Dashboard,
/// CLI).
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="TerminalHostResource"/> serves exactly one parent replica and owns three
/// stable socket paths under a per-replica directory beneath a per-target temporary base:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>{base}/{i}/dcp.sock</c> (<see cref="ProducerUdsPath"/>) — the producer socket.
///       The terminal host LISTENS on this path; DCP DIALS it to stream PTY traffic into the
///       host. <c>{i}</c> is the parent replica index, encoded into the path so each
///       per-replica host has its own unique paths even though the host process itself
///       is opaque to the replica index.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>{base}/{i}/host.sock</c> (<see cref="ConsumerUdsPath"/>) — the consumer socket.
///       The terminal host LISTENS on this path; viewers (Dashboard, CLI) DIAL it to attach.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>{base}/{i}/control.sock</c> (<see cref="ControlUdsPath"/>) — the control socket.
///       The terminal host LISTENS on this path; the AppHost DIALS it for status/shutdown
///       RPC. (See <see cref="Aspire.Shared.TerminalHost.TerminalHostControlProtocol"/>.)
///     </description>
///   </item>
/// </list>
/// <para>
/// Connection direction (consistent across all three sockets): the terminal host is the
/// LISTENER everywhere; DCP, viewers, and the AppHost are the DIALERS. This is also true
/// of <c>TerminalSpec.UdsPath</c> in the DCP API.
/// </para>
/// <para>
/// All per-replica hosts for a given parent share the same <see cref="BaseDirectory"/> so
/// that when the AppHost shuts down a single recursive deletion of <see cref="BaseDirectory"/>
/// cleans up every replica's sockets.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, ParentReplicaIndex = {ParentReplicaIndex}, BaseDirectory = {BaseDirectory}")]
public sealed class TerminalHostLayout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalHostLayout"/> class for a
    /// single parent-resource replica.
    /// </summary>
    /// <param name="baseDirectory">The base directory that contains the per-replica sub-directory holding all three socket paths.</param>
    /// <param name="parentReplicaIndex">The zero-based index of the parent replica this layout serves.</param>
    /// <param name="producerUdsPath">The producer (host-listens-on, DCP-dials) UDS path.</param>
    /// <param name="consumerUdsPath">The consumer (host-listens-on, viewers-dial) UDS path.</param>
    /// <param name="controlUdsPath">The control (host-listens-on, AppHost-dials) UDS path.</param>
    public TerminalHostLayout(string baseDirectory, int parentReplicaIndex, string producerUdsPath, string consumerUdsPath, string controlUdsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(parentReplicaIndex);
        ArgumentException.ThrowIfNullOrEmpty(producerUdsPath);
        ArgumentException.ThrowIfNullOrEmpty(consumerUdsPath);
        ArgumentException.ThrowIfNullOrEmpty(controlUdsPath);

        BaseDirectory = baseDirectory;
        ParentReplicaIndex = parentReplicaIndex;
        ProducerUdsPath = producerUdsPath;
        ConsumerUdsPath = consumerUdsPath;
        ControlUdsPath = controlUdsPath;
    }

    /// <summary>
    /// Gets the base directory that contains the per-replica sub-directory holding the
    /// socket paths in this layout. Shared across all per-replica hosts of the same parent
    /// so cleanup is a single recursive delete.
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// Gets the zero-based index of the parent replica this host serves. Encoded into
    /// each socket path so per-replica hosts of the same parent get unique paths.
    /// </summary>
    public int ParentReplicaIndex { get; }

    /// <summary>
    /// Gets the producer UDS path. The terminal host LISTENS on this path; DCP DIALS it.
    /// </summary>
    public string ProducerUdsPath { get; }

    /// <summary>
    /// Gets the consumer UDS path. The terminal host LISTENS on this path; viewers
    /// (Dashboard, CLI) DIAL it.
    /// </summary>
    public string ConsumerUdsPath { get; }

    /// <summary>
    /// Gets the control UDS path. The terminal host LISTENS on this path; the AppHost
    /// DIALS it for status/shutdown RPC.
    /// </summary>
    public string ControlUdsPath { get; }

    /// <summary>
    /// Gets the parent replica index as an invariant-culture string. Convenience for
    /// callers that need to log or include the index in identifiers.
    /// </summary>
    public string ParentReplicaIndexString => ParentReplicaIndex.ToString(CultureInfo.InvariantCulture);
}
