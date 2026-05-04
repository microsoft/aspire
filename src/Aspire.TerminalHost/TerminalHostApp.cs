// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// In-process entry point for the Aspire terminal host. Owns the per-replica
/// relay terminals, the control listener, and the lifecycle/shutdown handshake.
///
/// Exposed as a class so tests can drive the host without spawning a process.
/// </summary>
internal sealed class TerminalHostApp : IAsyncDisposable
{
    private readonly TerminalHostArgs _args;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<TerminalReplica> _replicas = new();
    private readonly object _gate = new();
    private TerminalHostControlListener? _controlListener;
    private bool _disposed;

    public TerminalHostApp(TerminalHostArgs args, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _args = args;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TerminalHostApp>();
    }

    public int ReplicaCount => _args.ReplicaCount;

    /// <summary>
    /// Snapshot of all replica states, suitable for marshalling to the AppHost
    /// via the control protocol.
    /// </summary>
    public TerminalHostReplicaInfo[] SnapshotReplicas()
    {
        lock (_gate)
        {
            var snap = new TerminalHostReplicaInfo[_replicas.Count];
            for (var i = 0; i < _replicas.Count; i++)
            {
                var r = _replicas[i];
                snap[i] = new TerminalHostReplicaInfo
                {
                    Index = r.Index,
                    ProducerUdsPath = r.ProducerUdsPath,
                    ConsumerUdsPath = r.ConsumerUdsPath,
                    IsAlive = r.IsAlive,
                    ExitCode = r.ExitCode,
                };
            }
            return snap;
        }
    }

    /// <summary>
    /// Starts each replica relay and the control listener, then waits for either
    /// the external cancellation token or a shutdown request to fire. Returns the
    /// process exit code.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (_args.Shell is { } shell)
        {
            _logger.LogInformation(
                "Aspire terminal host starting: {Replicas} replica(s), shell hint='{Shell}', size={Cols}x{Rows}.",
                _args.ReplicaCount, shell, _args.Columns, _args.Rows);
        }
        else
        {
            _logger.LogInformation(
                "Aspire terminal host starting: {Replicas} replica(s), size={Cols}x{Rows}.",
                _args.ReplicaCount, _args.Columns, _args.Rows);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);
        var token = linkedCts.Token;

        try
        {
            // Start replicas first, then the control listener; that way as soon as
            // the AppHost can connect to control, all consumer UDS paths are bound.
            for (var i = 0; i < _args.ReplicaCount; i++)
            {
                var replicaLogger = _loggerFactory.CreateLogger($"Aspire.TerminalHost.Replica[{i}]");
                var replica = TerminalReplica.Start(
                    i,
                    _args.ProducerUdsPaths[i],
                    _args.ConsumerUdsPaths[i],
                    _args.Columns,
                    _args.Rows,
                    replicaLogger,
                    token);
                lock (_gate)
                {
                    _replicas.Add(replica);
                }
            }

            _controlListener = new TerminalHostControlListener(
                _args.ControlUdsPath,
                new TerminalHostControlRpcTarget(this),
                _loggerFactory.CreateLogger<TerminalHostControlListener>());
            await _controlListener.StartAsync().ConfigureAwait(false);

            _logger.LogInformation("Terminal host ready.");

            await WaitForShutdownAsync(token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal host failed.");
            return 1;
        }
        finally
        {
            await TearDownAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        // Wait for external cancellation or an explicit shutdown request via the
        // control protocol. We do not auto-exit when all replicas have exited:
        // a replica failing/disconnecting is recoverable in normal operation
        // (DCP may relaunch the upstream PTY), and DCP is responsible for
        // tearing down the host when the resource is fully stopped.
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Signals a graceful shutdown. Returns immediately;
    /// <see cref="RunAsync(CancellationToken)"/> will exit shortly after.
    /// </summary>
    public void RequestShutdown()
    {
        if (!_shutdownCts.IsCancellationRequested)
        {
            _logger.LogInformation("Shutdown requested.");
            _shutdownCts.Cancel();
        }
    }

    private async Task TearDownAsync()
    {
        if (_controlListener is not null)
        {
            await _controlListener.DisposeAsync().ConfigureAwait(false);
            _controlListener = null;
        }

        TerminalReplica[] toDispose;
        lock (_gate)
        {
            toDispose = [.. _replicas];
            _replicas.Clear();
        }
        foreach (var r in toDispose)
        {
            try
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disposing replica {Index}.", r.Index);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestShutdown();
        await TearDownAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    /// <summary>
    /// Convenience entry point used by both <c>Program.Main</c> and tests.
    /// Catches argument-parsing errors and writes a friendly message to stderr.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        TerminalHostArgs parsed;
        try
        {
            parsed = TerminalHostArgs.Parse(args);
        }
        catch (TerminalHostArgsException ex)
        {
            await Console.Error.WriteLineAsync($"[Aspire.TerminalHost] {ex.Message}")
                .ConfigureAwait(false);
            return 64; // EX_USAGE
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new StderrLoggerProvider());
            builder.SetMinimumLevel(LogLevel.Information);
        });

        await using var app = new TerminalHostApp(parsed, loggerFactory);
        return await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
