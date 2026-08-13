// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the Unix domain socket and metadata paths for one terminal host.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, ParentReplicaIndex = {ParentReplicaIndex}, ReplicaId = {ReplicaId}")]
internal sealed class TerminalHostLayout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalHostLayout"/> class for a
    /// single parent-resource replica.
    /// </summary>
    /// <param name="replicaId">The 11-character base64url replica identifier (shared filename prefix).</param>
    /// <param name="parentReplicaIndex">The zero-based index of the parent replica this layout serves.</param>
    /// <param name="producerUdsPath">The producer (host-listens-on, DCP-dials) UDS path.</param>
    /// <param name="consumerUdsPath">The consumer (host-listens-on, viewers-dial) UDS path.</param>
    /// <param name="controlUdsPath">The control (host-listens-on, AppHost-dials) UDS path.</param>
    /// <param name="metadataPath">The per-replica metadata sidecar (JSON) path.</param>
    public TerminalHostLayout(
        string replicaId,
        int parentReplicaIndex,
        string producerUdsPath,
        string consumerUdsPath,
        string controlUdsPath,
        string metadataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        ArgumentOutOfRangeException.ThrowIfNegative(parentReplicaIndex);
        ArgumentException.ThrowIfNullOrEmpty(producerUdsPath);
        ArgumentException.ThrowIfNullOrEmpty(consumerUdsPath);
        ArgumentException.ThrowIfNullOrEmpty(controlUdsPath);
        ArgumentException.ThrowIfNullOrEmpty(metadataPath);

        ReplicaId = replicaId;
        ParentReplicaIndex = parentReplicaIndex;
        ProducerUdsPath = producerUdsPath;
        ConsumerUdsPath = consumerUdsPath;
        ControlUdsPath = controlUdsPath;
        MetadataPath = metadataPath;
    }

    /// <summary>
    /// Gets the 11-character base64url replica identifier. All four per-replica files
    /// share this prefix (e.g. <c>{ReplicaId}.dcp.sock</c>), so cleanup is a simple
    /// directory glob.
    /// </summary>
    public string ReplicaId { get; }

    /// <summary>
    /// Gets the zero-based index of the parent replica this host serves. Folded into
    /// <see cref="ReplicaId"/> so per-replica hosts of the same parent resource get
    /// distinct ids.
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
    /// Gets the path of the per-replica metadata sidecar (JSON). Written by the AppHost
    /// after the host process starts so out-of-band tools (the <c>aspire terminal</c>
    /// CLI, log scrapers) can discover terminals without holding a backchannel.
    /// </summary>
    public string MetadataPath { get; }

    /// <summary>
    /// Gets the parent replica index as an invariant-culture string. Convenience for
    /// callers that need to log or include the index in identifiers.
    /// </summary>
    public string ParentReplicaIndexString => ParentReplicaIndex.ToString(CultureInfo.InvariantCulture);
}
