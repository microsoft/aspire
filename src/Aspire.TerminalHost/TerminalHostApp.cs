// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// In-process entry point for the Aspire terminal host. Owns the single
/// per-replica relay terminal, the control listener, and the lifecycle/shutdown
/// handshake.
///
/// Each <c>aspire.terminalhost</c> process serves exactly one replica. Replica
/// fan-out happens at the AppHost level: a target resource with N replicas
/// causes N independent terminal host processes to be spawned, each with its
/// own producer/consumer/control UDS triple. The host has no notion of its
/// global replica index — that's encoded in the UDS paths and is opaque here.
///
/// Exposed as a class so tests can drive the host without spawning a process.
/// </summary>
public sealed class TerminalHostApp : IAsyncDisposable
{
    private readonly TerminalHostArgs _args;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _gate = new();
    private TerminalReplica? _replica;
    private TerminalHostControlListener? _controlListener;
    private bool _disposed;

    internal TerminalHostApp(TerminalHostArgs args, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _args = args;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TerminalHostApp>();
    }

    /// <summary>
    /// Snapshot of the host's single replica session, suitable for marshalling
    /// to the AppHost via the control protocol.
    /// </summary>
    internal TerminalHostSessionInfo SnapshotSession()
    {
        TerminalReplica? replica;
        lock (_gate)
        {
            replica = _replica;
        }

        if (replica is null)
        {
            // Pre-start (replica not yet created). Report a placeholder consistent with
            // "no producer connected" so callers can still read configured paths.
            return new TerminalHostSessionInfo
            {
                ProducerUdsPath = _args.ProducerUdsPath,
                ConsumerUdsPath = _args.ConsumerUdsPath,
                IsAlive = false,
                ExitCode = null,
                ProducerConnected = false,
                RestartCount = 0,
                CurrentColumns = _args.Columns,
                CurrentRows = _args.Rows,
                AttachedPeerCount = 0,
                Peers = Array.Empty<TerminalHostPeerInfo>(),
            };
        }

        return new TerminalHostSessionInfo
        {
            ProducerUdsPath = replica.ProducerUdsPath,
            ConsumerUdsPath = replica.ConsumerUdsPath,
            IsAlive = replica.IsAlive,
            ExitCode = replica.ExitCode,
            ProducerConnected = replica.ProducerConnected,
            RestartCount = replica.RestartCount,
            CurrentColumns = replica.CurrentColumns,
            CurrentRows = replica.CurrentRows,
            AttachedPeerCount = replica.AttachedPeerCount,
            Peers = replica.SnapshotPeers(),
        };
    }

    /// <summary>
    /// Starts the replica relay and the control listener, then waits for either
    /// the external cancellation token or a shutdown request to fire. Returns the
    /// process exit code.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (_args.Shell is { } shell)
        {
            _logger.LogInformation(
                "Aspire terminal host starting: shell hint='{Shell}', size={Cols}x{Rows}, producer='{Producer}', consumer='{Consumer}'.",
                shell, _args.Columns, _args.Rows, _args.ProducerUdsPath, _args.ConsumerUdsPath);
        }
        else
        {
            _logger.LogInformation(
                "Aspire terminal host starting: size={Cols}x{Rows}, producer='{Producer}', consumer='{Consumer}'.",
                _args.Columns, _args.Rows, _args.ProducerUdsPath, _args.ConsumerUdsPath);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);
        var token = linkedCts.Token;

        try
        {
            // Start the replica first, then the control listener; that way as soon as
            // the AppHost can connect to control, the consumer UDS is bound.
            var replicaLogger = _loggerFactory.CreateLogger("Aspire.TerminalHost.Replica");
            var replica = TerminalReplica.Start(
                _args.ProducerUdsPath,
                _args.ConsumerUdsPath,
                _args.Columns,
                _args.Rows,
                replicaLogger,
                token);
            lock (_gate)
            {
                _replica = replica;
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
        // control protocol. We do not auto-exit when the replica's producer disconnects:
        // that is recoverable in normal operation (DCP may relaunch the upstream PTY),
        // and DCP is responsible for tearing down the host when the resource is fully
        // stopped.
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

        TerminalReplica? toDispose;
        lock (_gate)
        {
            toDispose = _replica;
            _replica = null;
        }
        if (toDispose is not null)
        {
            try
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disposing replica.");
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
