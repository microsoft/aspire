// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Automation;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// An <see cref="IAspireTerminal"/> over a terminal that belongs to a resource replica.
/// </summary>
/// <remarks>
/// <para>
/// Unlike an AppHost terminal, the workload here runs in a per-replica terminal host process and the AppHost
/// is only ever a peer of it. Automation is served by joining that host's consumer socket as an ordinary HMP1
/// client — the same socket the CLI's <c>terminal attach</c> and the dashboard's resource terminal view dial —
/// and running the standard automator against the resulting terminal. The screen is replicated to every peer,
/// so a client-side terminal is a faithful mirror of the producer's.
/// </para>
/// <para>
/// The connection is made on first use rather than at construction. Listing terminals must not cost a socket
/// connection per replica, and the connection shows up in the host's peer roster, so an idle handle that
/// nobody is automating should leave no trace.
/// </para>
/// <para>
/// The peer always joins as <see cref="Hmp1Role.Secondary"/>. Only the primary peer's dimensions drive the
/// producer's PTY, and a secondary is still fully interactive, so automation can read and type without
/// resizing the grid out from under a human who is watching the same terminal.
/// </para>
/// </remarks>
internal sealed class ResourceAspireTerminal : IAspireTerminal
{
    /// <summary>
    /// How long to wait for the HMP1 handshake before treating the terminal host as unreachable.
    /// </summary>
    /// <remarks>
    /// The socket is local, so a healthy host completes the handshake in milliseconds. This bound exists for
    /// the case where the host process is gone but its socket file has not been cleaned up, where a connect
    /// would otherwise hang an automation call indefinitely.
    /// </remarks>
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _consumerUdsPath;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposalCts = new();
    private readonly object _gate = new();

    private ConnectionAttempt? _connection;
    private Task? _disposeTask;
    private bool _disposed;

    public ResourceAspireTerminal(string id, string title, string consumerUdsPath, ILogger logger)
    {
        Id = id;
        Title = title;
        _consumerUdsPath = consumerUdsPath;
        _logger = logger;
    }

    public string Id { get; }

    public string Title { get; }

    public TerminalOwner Owner => TerminalOwner.Resource;

    public TerminalPlacement Placement => TerminalPlacement.ResourceView;

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <remarks>
    /// The workload is started by the resource it belongs to, so there is nothing for the AppHost to start.
    /// </remarks>
    public void Start()
    {
    }

    /// <remarks>
    /// A resource terminal is displayed on its own resource's terminal view, which the dashboard navigates to
    /// directly. There is no dock tab for this to activate.
    /// </remarks>
    public void Show()
    {
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await TerminalAutomation.SendTextAsync(connection.Automator, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await TerminalAutomation.SendKeyAsync(connection.Terminal, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await TerminalAutomation.WaitForTextAsync(connection.Automator, Id, text, timeout, cancellationToken).ConfigureAwait(false);
    }

    public string GetScreenText()
    {
        Hex1bTerminalAutomator? automator;
        lock (_gate)
        {
            automator = _connection?.Automator;
        }

        return TerminalAutomation.GetScreenText(automator);
    }

    /// <summary>
    /// Shares a live connection, replacing failed or disconnected peers on a later automation call.
    /// </summary>
    private async Task<TerminalConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConnectionAttempt connection;
            bool disconnected;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_connection is null)
                {
                    _connection = new ConnectionAttempt(_disposalCts.Token);
                    _connection.Completion = RunConnectionAsync(_connection);
                }

                connection = _connection;
                disconnected = connection.Disconnected;
            }

            if (!disconnected)
            {
                // A caller's cancellation only abandons its wait, not the connection shared by other callers.
                // Never replay an automation command: a failed attempt is surfaced to its original caller.
                return await connection.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Finish releasing the old peer before opening another. Keeping it registered until cleanup ends
            // also lets DisposeAsync await every peer, including a connection that failed during startup.
            await connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
        }
    }

    private async Task RunConnectionAsync(ConnectionAttempt connection)
    {
        // Building and running Hex1b must not happen inline under _gate.
        await Task.Yield();

        var cancellationToken = connection.Cancellation.Token;
        var connected = new TaskCompletionSource<(int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Hex1bTerminal? terminal = null;
        Task? runTask = null;
        Exception? failure = null;
        var expectedCancellation = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            terminal = Hex1bTerminal.CreateBuilder()
                // The AppHost has no controlling terminal. Headless suppresses local console I/O but still
                // maintains the replicated screen used by automation.
                .WithHeadless()
                .WithDimensions(80, 24)
                .WithHmp1UdsClient(_consumerUdsPath, options =>
                {
                    options.DisplayName = $"apphost-automation:{Id}";
                    options.DefaultRole = Hmp1Role.Secondary;

                    options.OnConnected = (e, _) =>
                    {
                        Resize(terminal, e.Width, e.Height);
                        connected.TrySetResult((e.Width, e.Height));
                        return Task.CompletedTask;
                    };

                    options.OnRemoteResized = (e, _) =>
                    {
                        Resize(terminal, e.Width, e.Height);
                        return Task.CompletedTask;
                    };

                    options.OnDisconnected = _ =>
                    {
                        MarkDisconnected(connection);
                        connection.Cancellation.Cancel();
                        return Task.CompletedTask;
                    };
                })
                .Build();

            _logger.LogDebug("Connecting AppHost automation to resource terminal {TerminalId} at '{ConsumerPath}'.", Id, _consumerUdsPath);
            cancellationToken.ThrowIfCancellationRequested();
            runTask = terminal.RunAsync(cancellationToken);

            // A missing socket faults the pump before the handshake. Observe either result so an immediate
            // transport failure is not reported as a ten-second handshake timeout.
            var completed = await Task.WhenAny(connected.Task, runTask).WaitAsync(s_connectTimeout, cancellationToken).ConfigureAwait(false);
            if (completed == runTask)
            {
                await runTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The connection to the terminal host for terminal '{Id}' closed before the terminal was ready.");
            }

            var (width, height) = await connected.Task.ConfigureAwait(false);
            var automator = new Hex1bTerminalAutomator(terminal, TerminalAutomation.DefaultTimeout);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (connection.Disconnected)
                {
                    throw new InvalidOperationException($"The terminal host for terminal '{Id}' disconnected during initialization.");
                }

                connection.Automator = automator;
                connection.Ready.TrySetResult(new TerminalConnection(terminal, automator));
            }

            _logger.LogDebug("Connected to resource terminal {TerminalId} ({Width}x{Height}).", Id, width, height);
            await runTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            expectedCancellation = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
        }
        finally
        {
            MarkDisconnected(connection);
            await connection.Cancellation.CancelAsync().ConfigureAwait(false);
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Preserve the handshake failure if one already occurred; otherwise report the pump's
                    // failure below. In both cases the task is observed before disposing its terminal.
                    failure ??= ex;
                }
            }

            if (terminal is not null)
            {
                try
                {
                    await terminal.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disposing the automation peer for resource terminal {TerminalId} failed unexpectedly.", Id);
                    failure ??= ex;
                }
            }

            connection.Cancellation.Dispose();
        }

        if (expectedCancellation)
        {
            _logger.LogDebug("AppHost automation peer for resource terminal {TerminalId} stopped after disposal or a terminal host disconnect.", Id);
            connection.Ready.TrySetCanceled(cancellationToken);
        }
        else if (failure is not null)
        {
            if (failure is TimeoutException)
            {
                failure = new InvalidOperationException(
                    $"Timed out connecting to the terminal host for terminal '{Id}' at '{_consumerUdsPath}'. The resource replica may not be running.", failure);
            }

            if (connection.Ready.TrySetException(failure))
            {
                _logger.LogDebug(failure, "Connecting the AppHost automation peer to resource terminal {TerminalId} failed; a later call can retry.", Id);
            }
            else
            {
                _logger.LogWarning(failure, "AppHost automation peer for resource terminal {TerminalId} ended unexpectedly; a later call can reconnect.", Id);
            }
        }

        static void Resize(Hex1bTerminal? target, int width, int height)
            => target?.Resize(Math.Max(1, width), Math.Max(1, height));
    }

    private void MarkDisconnected(ConnectionAttempt connection)
    {
        lock (_gate)
        {
            connection.Disconnected = true;
            connection.Automator = null;
        }
    }

    /// <summary>
    /// Disconnects the AppHost's automation peer. The resource's workload is unaffected.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_connection is { } connection)
            {
                connection.Automator = null;
            }

            return new ValueTask(_disposeTask ??= DisposeCoreAsync(_connection));
        }
    }

    private async Task DisposeCoreAsync(ConnectionAttempt? connection)
    {
        await Task.Yield();
        await _disposalCts.CancelAsync().ConfigureAwait(false);

        if (connection is not null)
        {
            await connection.Completion.ConfigureAwait(false);
        }

        _disposalCts.Dispose();
    }

    private sealed class ConnectionAttempt
    {
        public ConnectionAttempt(CancellationToken disposalToken)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(disposalToken);
            // Every caller may abandon its wait before connection fails. Observe that failure even when
            // nobody remains to await Ready; future calls still replace the failed attempt.
            _ = Ready.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<TerminalConnection> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; set; } = Task.CompletedTask;
        public Hex1bTerminalAutomator? Automator { get; set; }
        public bool Disconnected { get; set; }
    }

    private sealed record TerminalConnection(Hex1bTerminal Terminal, Hex1bTerminalAutomator Automator);
}
