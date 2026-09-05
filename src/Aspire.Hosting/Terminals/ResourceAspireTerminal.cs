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
    private readonly CancellationTokenSource _clientCts = new();
    private readonly object _gate = new();

    private Task<TerminalConnection>? _connectTask;
    private Task? _runTask;
    private Hex1bTerminalAutomator? _automator;
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
            automator = _automator;
        }

        return TerminalAutomation.GetScreenText(automator);
    }

    /// <summary>
    /// Connects to the replica's terminal host on first use, and returns the same connection thereafter.
    /// </summary>
    private Task<TerminalConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        Task<TerminalConnection> connectTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cache the task rather than the result so concurrent callers share one connection attempt, and a
            // failed attempt is not retried behind the back of the caller that observed the failure.
            connectTask = _connectTask ??= ConnectAsync();
        }

        return connectTask.WaitAsync(cancellationToken);
    }

    private async Task<TerminalConnection> ConnectAsync()
    {
        // Never run the connect inline under _gate.
        await Task.Yield();

        var connected = new TaskCompletionSource<(int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The handshake can be failed after the connect timeout has already given up on it — by the pump
        // ending, or by a disconnect callback. Nothing would await it by then, and an unobserved faulted task
        // surfaces on TaskScheduler.UnobservedTaskException, which is a process-wide event an AppHost may
        // treat as fatal. Observing it here is harmless: an awaiter still sees the exception.
        _ = connected.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Hex1bTerminal? terminal = null;

        terminal = Hex1bTerminal.CreateBuilder()
            // The AppHost has no controlling terminal, so the client must not try to drive one. Headless
            // discards output at the adapter and supplies no input; the screen buffer automation reads is
            // still maintained, because it is built from the remote's output before presentation.
            .WithHeadless()
            // An arbitrary opener. The handshake reports the producer's real grid and the terminal is resized
            // to match before the automator is handed out, so nothing ever reads this size.
            .WithDimensions(80, 24)
            .WithHmp1UdsClient(_consumerUdsPath, options =>
            {
                // Named so a human running `aspire terminal ps --verbose` can tell an automation peer apart
                // from a dashboard tab or an attached CLI.
                options.DisplayName = $"apphost-automation:{Id}";
                options.DefaultRole = Hmp1Role.Secondary;

                options.OnConnected = (e, _) =>
                {
                    Resize(terminal, e.Width, e.Height);
                    connected.TrySetResult((e.Width, e.Height));
                    return Task.CompletedTask;
                };

                // Follow the producer's grid when another peer resizes it, so a screen read after a human
                // resizes their dashboard tab is not silently clipped to the old dimensions.
                options.OnRemoteResized = (e, _) =>
                {
                    Resize(terminal, e.Width, e.Height);
                    return Task.CompletedTask;
                };

                options.OnDisconnected = _ =>
                {
                    // The terminal host went away. Fail a handshake still in flight rather than letting it
                    // sit until the connect timeout.
                    connected.TrySetException(new InvalidOperationException(
                        $"The terminal host for terminal '{Id}' disconnected before the connection was established."));
                    return Task.CompletedTask;
                };
            })
            .Build();

        _logger.LogDebug("Connecting AppHost automation to resource terminal {TerminalId} at '{ConsumerPath}'.", Id, _consumerUdsPath);

        _runTask = RunClientAsync(terminal, connected);

        try
        {
            var (width, height) = await connected.Task.WaitAsync(s_connectTimeout).ConfigureAwait(false);
            _logger.LogDebug("Connected to resource terminal {TerminalId} ({Width}x{Height}).", Id, width, height);
        }
        catch (Exception ex)
        {
            // The pump owns the terminal once RunClientAsync is running, so tear it down through the same
            // path rather than disposing the terminal here and racing the pump.
            _clientCts.Cancel();

            if (ex is TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Timed out connecting to the terminal host for terminal '{Id}' at '{_consumerUdsPath}'. The resource replica may not be running.", ex);
            }

            throw;
        }

        var automator = new Hex1bTerminalAutomator(terminal, TerminalAutomation.DefaultTimeout);

        lock (_gate)
        {
            _automator = automator;
        }

        return new TerminalConnection(terminal, automator);

        static void Resize(Hex1bTerminal? target, int width, int height)
            => target?.Resize(Math.Max(1, width), Math.Max(1, height));
    }

    private async Task RunClientAsync(Hex1bTerminal terminal, TaskCompletionSource<(int Width, int Height)> connected)
    {
        try
        {
            await terminal.RunAsync(_clientCts.Token).ConfigureAwait(false);

            // The pump returning without the handshake having completed means the transport closed before the
            // terminal was usable. Nothing else would fail the handshake in that case, so it would otherwise
            // sit until the connect timeout.
            connected.TrySetException(new InvalidOperationException(
                $"The connection to the terminal host for terminal '{Id}' closed before the terminal was ready."));
        }
        catch (OperationCanceledException)
        {
            // Expected: the handle was disposed, or the connect attempt was abandoned.
            _logger.LogDebug("AppHost automation peer for resource terminal {TerminalId} was cancelled.", Id);
        }
        catch (Exception ex)
        {
            // Surface the real transport error to a handshake still in flight. A replica that is not running
            // leaves no socket to dial, which fails here immediately, and reporting it now is both faster and
            // more specific than letting the connect timeout elapse.
            if (connected.TrySetException(ex))
            {
                _logger.LogDebug(ex, "Connecting the AppHost automation peer to resource terminal {TerminalId} failed.", Id);
                return;
            }

            // Unexpected. The workload itself is unaffected — only this process's view of it is lost — so this
            // is a warning rather than an error, but it does mean subsequent automation calls read a dead screen.
            _logger.LogWarning(ex, "AppHost automation peer for resource terminal {TerminalId} ended unexpectedly.", Id);
        }
        finally
        {
            try
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing the automation peer for resource terminal {TerminalId} failed.", Id);
            }
        }
    }

    /// <summary>
    /// Disconnects the AppHost's automation peer. The resource's workload is unaffected.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runTask = _runTask;
            _automator = null;
        }

        await _clientCts.CancelAsync().ConfigureAwait(false);

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunClientAsync already logs and swallows; this only guards against a fault escaping the pump
                // itself, which must not turn disposal into a throwing operation.
                _logger.LogDebug(ex, "The automation peer for resource terminal {TerminalId} faulted while disconnecting.", Id);
            }
        }

        _clientCts.Dispose();
    }

    private sealed record TerminalConnection(Hex1bTerminal Terminal, Hex1bTerminalAutomator Automator);
}
