// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.TerminalHost;
using Hex1b;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// A single replica relay session inside the terminal host. The replica owns
/// a recycle loop that builds successive <see cref="Hex1bTerminal"/> instances
/// over the lifetime of the host process.
///
/// Each iteration of the loop:
/// <list type="number">
///   <item>Builds a fresh Hex1bTerminal that listens on the producer UDS and
///         serves on the consumer UDS.</item>
///   <item>Waits for DCP (the upstream PTY owner) to dial the producer UDS.
///         When that connection is accepted, <see cref="ProducerConnected"/>
///         flips to <c>true</c>.</item>
///   <item>Forwards bytes between producer and any number of viewers (Dashboard,
///         CLI) via Hex1b's HMP v1 multiplexing.</item>
///   <item>When the producer disconnects (process exit, transport error, etc.)
///         the inner <see cref="Hex1bTerminal.RunAsync(CancellationToken)"/>
///         returns. The terminal is disposed (which releases the UDS bindings
///         and tears down any attached viewer sessions), <see cref="LastExitCode"/>
///         is updated, <see cref="RestartCount"/> is incremented, and the loop
///         iterates to bind the same UDS paths again — ready for DCP to relaunch
///         the underlying process and dial back in.</item>
/// </list>
///
/// Connection direction note: the producer side has the terminal host listening
/// and DCP dialing, not the other way around. This guarantees the host is
/// receiving from the very first byte the PTY emits, and also lets the host
/// recycle without DCP having to re-coordinate; DCP just dials the same path
/// again and Hex1b's existing connect-retry semantics on the producer side
/// take care of the brief unbound window during a recycle.
///
/// One <see cref="TerminalReplica"/> is owned by the host per replica. They
/// are independent — a recycle on one replica does not affect the others.
/// </summary>
internal sealed class TerminalReplica : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Task _runTask;
    private readonly CancellationTokenSource _stopCts;
    private readonly object _gate = new();
    private readonly Dictionary<string, TerminalHostPeerInfo> _peers = new(StringComparer.Ordinal);
    private Hex1bTerminal? _currentTerminal;
    private bool _producerConnected;
    private int? _lastExitCode;
    private int _restartCount;
    private int _currentColumns;
    private int _currentRows;
    private bool _disposed;

    public int Index { get; }
    public string ProducerUdsPath { get; }
    public string ConsumerUdsPath { get; }
    public int Columns { get; }
    public int Rows { get; }

    /// <summary>
    /// True while the current Hex1bTerminal has an attached producer (DCP has
    /// dialed in and the upstream PTY is delivering bytes). False between
    /// recycles, before the first producer ever connects, or after the
    /// replica has been torn down. Distinct from "the replica slot is alive
    /// in the host" — the slot is always alive while the host is running.
    /// </summary>
    public bool ProducerConnected
    {
        get { lock (_gate) { return _producerConnected; } }
    }

    /// <summary>
    /// Exit code from the most recently-completed Hex1bTerminal cycle, or
    /// <c>null</c> if no cycle has completed yet. Updated each time the
    /// producer disconnects.
    /// </summary>
    public int? LastExitCode
    {
        get { lock (_gate) { return _lastExitCode; } }
    }

    /// <summary>
    /// Number of completed Hex1bTerminal cycles (i.e. number of times the
    /// producer has connected and then disconnected). Useful as a diagnostic
    /// signal for "has this resource restarted unexpectedly?".
    /// </summary>
    public int RestartCount
    {
        get { lock (_gate) { return _restartCount; } }
    }

    /// <summary>
    /// Backwards-compatible alias for callers that historically asked
    /// "is this replica running?". Today that question really means "is the
    /// upstream producer currently attached?", which is what we report.
    /// The replica object itself outlives any single Hex1bTerminal cycle.
    /// </summary>
    public bool IsAlive => ProducerConnected;

    /// <summary>
    /// Backwards-compatible alias for <see cref="LastExitCode"/>.
    /// </summary>
    public int? ExitCode => LastExitCode;

    /// <summary>
    /// Current terminal grid width in columns, as last negotiated by the active HMP1
    /// primary peer (via <c>OnResized</c>). Initialized from the AppHost-configured
    /// width and updated on every downstream resize.
    /// </summary>
    public int CurrentColumns
    {
        get { lock (_gate) { return _currentColumns; } }
    }

    /// <summary>
    /// Current terminal grid height in rows. See <see cref="CurrentColumns"/>.
    /// </summary>
    public int CurrentRows
    {
        get { lock (_gate) { return _currentRows; } }
    }

    /// <summary>
    /// Number of HMP1 viewer peers currently attached to this replica's consumer UDS.
    /// Maintained from <c>OnClientConnected</c> / <c>OnClientDisconnected</c> callbacks.
    /// Zero between cycles or before the first peer connects.
    /// </summary>
    public int AttachedPeerCount
    {
        get { lock (_gate) { return _peers.Count; } }
    }

    /// <summary>
    /// Snapshot of currently-attached HMP1 viewer peers, in dictionary order.
    /// </summary>
    public TerminalHostPeerInfo[] SnapshotPeers()
    {
        lock (_gate)
        {
            if (_peers.Count == 0)
            {
                return Array.Empty<TerminalHostPeerInfo>();
            }
            var snap = new TerminalHostPeerInfo[_peers.Count];
            var i = 0;
            foreach (var peer in _peers.Values)
            {
                snap[i++] = peer;
            }
            return snap;
        }
    }

    /// <summary>
    /// Task that completes when the replica's recycle loop exits (i.e. when
    /// the host is shutting down or the replica is being disposed).
    /// </summary>
    public Task RunTask => _runTask;

    private TerminalReplica(
        int index,
        string producerUdsPath,
        string consumerUdsPath,
        int columns,
        int rows,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Index = index;
        ProducerUdsPath = producerUdsPath;
        ConsumerUdsPath = consumerUdsPath;
        Columns = columns;
        Rows = rows;
        _currentColumns = columns;
        _currentRows = rows;
        _logger = logger;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RecycleLoopAsync(_stopCts.Token), _stopCts.Token);
    }

    /// <summary>
    /// Builds the relay terminal and starts its recycle loop. The loop runs
    /// in the background and only exits on cancellation or dispose.
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

        logger.LogInformation(
            "Starting replica {Index}: producer='{Producer}', consumer='{Consumer}'",
            index, producerUdsPath, consumerUdsPath);

        return new TerminalReplica(
            index, producerUdsPath, consumerUdsPath, columns, rows, logger, cancellationToken);
    }

    /// <summary>
    /// Top-level loop. One iteration = one Hex1bTerminal lifetime. The loop
    /// only exits on cancellation; producer disconnects are an expected
    /// recoverable transition that triggers an immediate rebind.
    /// </summary>
    private async Task RecycleLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            Hex1bTerminal? terminal = null;
            int exitCode;
            var failed = false;

            try
            {
                try
                {
                    terminal = BuildTerminal();
                }
                catch (Exception ex)
                {
                    // Building the Hex1bTerminal can fail for transient reasons
                    // (UDS path temporarily unwritable, transient I/O during
                    // disposal of the previous instance, etc.). Treat as a
                    // failed cycle and let the backoff handle it instead of
                    // letting the exception kill the recycle loop.
                    _logger.LogError(ex, "Replica {Index} BuildTerminal threw.", Index);
                    exitCode = -1;
                    failed = true;
                    goto AfterRun;
                }

                lock (_gate)
                {
                    _currentTerminal = terminal;
                    _producerConnected = false;
                }

                try
                {
                    exitCode = await terminal.RunAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Host is shutting down. Exit the loop without recording a cycle.
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Replica {Index} cycle threw.", Index);
                    exitCode = -1;
                    failed = true;
                }

                AfterRun:;
            }
            finally
            {
                if (terminal is not null)
                {
                    try
                    {
                        await terminal.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Replica {Index}: terminal dispose threw (ignored).", Index);
                    }
                }

                lock (_gate)
                {
                    _currentTerminal = null;
                    _producerConnected = false;
                    // Hex1b's HMP1 server fires OnClientDisconnected for every peer when the
                    // terminal is disposed, so _peers should already be empty here. Clear
                    // defensively to avoid carrying stale entries into the next cycle if any
                    // disconnect callback raced past dispose.
                    _peers.Clear();
                }
            }

            // We got here because RunAsync returned (producer disconnected or
            // an error occurred), not because we were cancelled.
            lock (_gate)
            {
                _lastExitCode = exitCode;
                _restartCount++;
            }

            if (failed)
            {
                consecutiveFailures++;
                _logger.LogInformation(
                    "Replica {Index} cycle ended with failure (consecutive={Count}); will rebind.",
                    Index, consecutiveFailures);
            }
            else
            {
                consecutiveFailures = 0;
                _logger.LogInformation(
                    "Replica {Index} producer disconnected (exit code {ExitCode}); rebinding for next producer.",
                    Index, exitCode);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Backoff: stay snappy on a clean producer-disconnect (typical case
            // when the resource is being restarted) but escalate when the cycle
            // keeps failing fast, so a wedged transport doesn't burn CPU/log.
            var delay = consecutiveFailures switch
            {
                0 => TimeSpan.FromMilliseconds(100),
                1 => TimeSpan.FromMilliseconds(250),
                2 => TimeSpan.FromMilliseconds(500),
                3 => TimeSpan.FromSeconds(1),
                4 => TimeSpan.FromSeconds(2),
                _ => TimeSpan.FromSeconds(5),
            };

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Builds a fresh Hex1bTerminal bound to this replica's UDS paths. Each
    /// call returns an unrelated, undisposed instance; the recycle loop owns
    /// dispose timing.
    /// </summary>
    private Hex1bTerminal BuildTerminal()
    {
        // Build the upstream workload adapter ourselves so we can plumb downstream
        // resize events (from the consumer-side multi-head server below) into
        // unconditional FrameResize writes upstream to DCP. See DcpUpstreamAdapter
        // for why Hex1b's stock Hmp1WorkloadAdapter cannot be used here (DCP is
        // a single-peer producer that doesn't speak multi-head, so the adapter's
        // IsPrimary gate would silently drop every resize forever).
        var upstream = new DcpUpstreamAdapter(
            async cct =>
            {
                await foreach (var stream in Hmp1Transports.ListenUnixSocket(ProducerUdsPath, cct).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _producerConnected = true;
                    }
                    return stream;
                }
                throw new OperationCanceledException("Producer UDS listener was cancelled before any client connected.");
            },
            _logger);

        upstream.Disconnected += () =>
        {
            lock (_gate)
            {
                _producerConnected = false;
            }
        };

        return Hex1bTerminal.CreateBuilder()
            .WithDimensions(Columns, Rows)
            .WithWorkload(upstream)
            .WithHmp1UdsServer(
                ConsumerUdsPath,
                srvOpts =>
                {
                    // Track every HMP1 peer that connects/disconnects so the host can answer
                    // "who's currently attached to this replica?" via the control RPC. PeerId is
                    // assigned by Hex1b at handshake time and is unique per connection; DisplayName
                    // is the optional ClientHello label (e.g. "aspire-cli:1234", "dashboard:abc12345").
                    srvOpts.OnClientConnected = (e, _) =>
                    {
                        lock (_gate)
                        {
                            _peers[e.PeerId] = new TerminalHostPeerInfo
                            {
                                PeerId = e.PeerId,
                                DisplayName = e.DisplayName,
                            };
                        }
                        return Task.CompletedTask;
                    };
                    srvOpts.OnClientDisconnected = (e, _) =>
                    {
                        lock (_gate)
                        {
                            _peers.Remove(e.PeerId);
                        }
                        return Task.CompletedTask;
                    };

                    // Bridge downstream → upstream resize. The consumer-side multi-head
                    // server fires OnResized whenever the current primary peer's dims
                    // change (RequestPrimary or explicit Resize from primary). Forward
                    // those dims as a raw FrameResize upstream so DCP runs ConPty.Resize
                    // and the underlying workload sees the new TIOCSWINSZ value. Without
                    // this hook the consumer-side presentation reflects the new dims but
                    // the actual PTY stays at whatever DCP started it at.
                    //
                    // Also persist the latest dimensions so `aspire terminal ps` and the
                    // dashboard can report the current grid size without round-tripping to
                    // every attached viewer.
                    srvOpts.OnResized = async (e, ct) =>
                    {
                        lock (_gate)
                        {
                            _currentColumns = e.Width;
                            _currentRows = e.Height;
                        }

                        try
                        {
                            await upstream.ResizeAsync(e.Width, e.Height, ct).ConfigureAwait(false);
                            _logger.LogDebug(
                                "Replica {Index}: forwarded downstream resize ({Width}x{Height}) to upstream PTY.",
                                Index, e.Width, e.Height);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Recycle loop is shutting down; drop quietly.
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex,
                                "Replica {Index}: forwarding downstream resize ({Width}x{Height}) upstream failed.",
                                Index, e.Width, e.Height);
                        }
                    };
                })
            .Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _stopCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replica {Index}: recycle loop terminated with an unexpected error.", Index);
        }

        _stopCts.Dispose();
    }
}
