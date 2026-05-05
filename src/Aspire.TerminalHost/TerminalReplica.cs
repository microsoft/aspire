// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// A single replica relay session inside the terminal host.
///
/// The relay is a <see cref="Hex1bTerminal"/> that:
/// <list type="number">
///   <item>Listens on the producer UDS for an HMP v1 server connection (DCP dials
///         in once the underlying PTY-attached process is ready).</item>
///   <item>Maintains internal terminal state via Hex1b's headless presentation.</item>
///   <item>Re-broadcasts HMP v1 frames over the consumer UDS so any number of
///         viewers (Dashboard, CLI) can attach with full state replay.</item>
/// </list>
///
/// Connection direction note: the producer side has the terminal host listening
/// and DCP dialing, not the other way around. This guarantees the host is
/// receiving from the very first byte the PTY emits — important for shells
/// whose initial prompt arrives before any dashboard viewer connects.
///
/// One <see cref="TerminalReplica"/> is owned by the host per replica. They are
/// independent — a crash or exit on one replica does not affect the others.
/// </summary>
internal sealed class TerminalReplica : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Hex1bTerminal _terminal;
    private readonly Task<int> _runTask;
    private bool _disposed;
    private int? _exitCode;

    public int Index { get; }
    public string ProducerUdsPath { get; }
    public string ConsumerUdsPath { get; }

    /// <summary>
    /// True until <see cref="_runTask"/> completes. The terminal is considered alive
    /// while it is actively relaying frames; once it completes (upstream EOF, error,
    /// or cancellation), the replica is marked dead.
    /// </summary>
    public bool IsAlive => !_runTask.IsCompleted;

    /// <summary>
    /// Exit code from the relay's <see cref="Hex1bTerminal.RunAsync(CancellationToken)"/>
    /// once it has completed. Null while alive.
    /// </summary>
    public int? ExitCode => _exitCode;

    /// <summary>
    /// Task that completes when the relay exits.
    /// </summary>
    public Task<int> RunTask => _runTask;

    private TerminalReplica(
        int index,
        string producerUdsPath,
        string consumerUdsPath,
        Hex1bTerminal terminal,
        Task<int> runTask,
        ILogger logger)
    {
        Index = index;
        ProducerUdsPath = producerUdsPath;
        ConsumerUdsPath = consumerUdsPath;
        _terminal = terminal;
        _runTask = runTask;
        _logger = logger;
    }

    /// <summary>
    /// Builds the relay terminal and starts its run loop. The relay does not
    /// connect synchronously — the producer connection happens lazily inside
    /// the Hex1b workload, so this method always returns quickly.
    /// </summary>
    public static TerminalReplica Start(
        int index,
        string producerUdsPath,
        string consumerUdsPath,
        int columns,
        int rows,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerUdsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerUdsPath);
        ArgumentNullException.ThrowIfNull(logger);

        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(columns, rows)
            // Producer side: terminal host LISTENS on producerUdsPath; DCP
            // dials in. Hex1b plays the HMP1 protocol CLIENT role over that
            // accepted stream (consuming Hello/Output/StateSync/Exit and
            // forwarding Input/Resize back). Composing WithHmp1Client with
            // a listen-and-accept transport lets us flip the TCP direction
            // without flipping the HMP1 protocol direction (DCP, holding
            // the PTY, must remain the protocol server).
            .WithHmp1Client(async ct =>
            {
                await foreach (var stream in Hmp1Transports.ListenUnixSocket(producerUdsPath, ct).ConfigureAwait(false))
                {
                    return stream;
                }
                throw new OperationCanceledException("Producer UDS listener was cancelled before any client connected.");
            })
            .WithHmp1UdsServer(consumerUdsPath)
            .Build();

        logger.LogInformation(
            "Starting replica {Index}: producer='{Producer}', consumer='{Consumer}'",
            index, producerUdsPath, consumerUdsPath);

        var runTask = Task.Run(async () =>
        {
            try
            {
                var code = await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Replica {Index} exited with code {ExitCode}.", index, code);
                return code;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Replica {Index} cancelled.", index);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Replica {Index} terminated with an error.", index);
                return -1;
            }
        }, cancellationToken);

        var replica = new TerminalReplica(
            index, producerUdsPath, consumerUdsPath, terminal, runTask, logger);

        _ = runTask.ContinueWith(
            t => replica._exitCode = t.IsCompletedSuccessfully ? t.Result : -1,
            TaskScheduler.Default);

        return replica;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _terminal.DisposeAsync().ConfigureAwait(false);
    }
}
