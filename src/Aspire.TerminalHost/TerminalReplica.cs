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
///   <item>Consumes HMP v1 frames from the producer UDS (DCP listens here).</item>
///   <item>Maintains internal terminal state via Hex1b's headless presentation.</item>
///   <item>Re-broadcasts HMP v1 frames over the consumer UDS so any number of
///         viewers (Dashboard, CLI) can attach with full state replay.</item>
/// </list>
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
            .WithHmp1UdsClient(producerUdsPath)
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
