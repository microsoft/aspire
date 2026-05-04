// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the Unix domain socket layout used by a <see cref="TerminalHostResource"/> to
/// bridge per-replica PTY traffic between DCP and clients (Dashboard, CLI).
/// </summary>
/// <remarks>
/// <para>
/// For a target resource with <c>N</c> replicas, the layout owns <c>2N + 1</c> stable
/// socket paths under a per-run temporary directory:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>{base}/dcp/r{i}.sock</c> — the producer socket for replica <c>i</c>. DCP listens
///       on this path and the terminal host connects as an HMP v1 client to consume PTY output
///       and forward input.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>{base}/host/r{i}.sock</c> — the consumer socket for replica <c>i</c>. The terminal
///       host listens on this path as an HMP v1 server and viewers (Dashboard, CLI) connect to
///       attach.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>{base}/control.sock</c> — a single control socket that the terminal host listens on
///       for the AppHost backchannel to query the live replica list and request shutdown.
///     </description>
///   </item>
/// </list>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, ReplicaCount = {ReplicaCount}, BaseDirectory = {BaseDirectory}")]
public sealed class TerminalHostLayout
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalHostLayout"/> class.
    /// </summary>
    /// <param name="baseDirectory">The base directory that owns all socket paths in this layout.</param>
    /// <param name="producerUdsPaths">The producer (DCP-listens-on) UDS path for each replica.</param>
    /// <param name="consumerUdsPaths">The consumer (host-listens-on) UDS path for each replica.</param>
    /// <param name="controlUdsPath">The control UDS path for AppHost ↔ terminal host RPC.</param>
    public TerminalHostLayout(string baseDirectory, IReadOnlyList<string> producerUdsPaths, IReadOnlyList<string> consumerUdsPaths, string controlUdsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentNullException.ThrowIfNull(producerUdsPaths);
        ArgumentNullException.ThrowIfNull(consumerUdsPaths);
        ArgumentException.ThrowIfNullOrEmpty(controlUdsPath);

        if (producerUdsPaths.Count == 0)
        {
            throw new ArgumentException("At least one producer UDS path is required.", nameof(producerUdsPaths));
        }

        if (producerUdsPaths.Count != consumerUdsPaths.Count)
        {
            throw new ArgumentException(
                $"Producer ({producerUdsPaths.Count}) and consumer ({consumerUdsPaths.Count}) UDS path counts must match.",
                nameof(consumerUdsPaths));
        }

        BaseDirectory = baseDirectory;
        ProducerUdsPaths = producerUdsPaths;
        ConsumerUdsPaths = consumerUdsPaths;
        ControlUdsPath = controlUdsPath;
    }

    /// <summary>
    /// Gets the number of replicas this layout was sized for. Equal to the length of
    /// <see cref="ProducerUdsPaths"/> and <see cref="ConsumerUdsPaths"/>.
    /// </summary>
    public int ReplicaCount => ProducerUdsPaths.Count;

    /// <summary>
    /// Gets the base directory that owns the socket paths in this layout.
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// Gets the producer UDS path for each replica. DCP listens on these paths and the
    /// terminal host connects to them as an HMP v1 client.
    /// </summary>
    public IReadOnlyList<string> ProducerUdsPaths { get; }

    /// <summary>
    /// Gets the consumer UDS path for each replica. The terminal host listens on these paths
    /// as an HMP v1 server and viewers connect to them.
    /// </summary>
    public IReadOnlyList<string> ConsumerUdsPaths { get; }

    /// <summary>
    /// Gets the control UDS path. The terminal host listens on this path for AppHost RPC
    /// (replica enumeration, shutdown).
    /// </summary>
    public string ControlUdsPath { get; }
}
