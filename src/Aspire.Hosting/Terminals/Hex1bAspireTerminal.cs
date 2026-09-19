// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Automation;
using Hex1b.Reflow;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRETERMINAL001 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The Hex1b-backed implementation of <see cref="AspireTerminal"/>.
/// </summary>
/// <remarks>
/// Each viewer is attached to the same HMP1 presentation adapter, allowing several dashboard views to
/// share one terminal. The AppHost owns the workload, so terminal state survives a viewer disconnecting.
/// </remarks>
internal sealed class Hex1bAspireTerminal : ITerminalBackend
{
    private readonly HashSet<Task> _clientTasks = [];

    // Cancellation requests a stop; workload completion updates viewers; session completion reports that
    // teardown has finished. Keeping these separate lets viewers display an ended state without allowing
    // the gRPC handler to dispose a transport that Hex1b is still accessing.
    private readonly CancellationTokenSource _workloadCts = new();
    private readonly TaskCompletionSource _workloadEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Aspire.Hosting targets net8.0, which predates System.Threading.Lock, so this is a plain monitor gate.
    private readonly object _gate = new();

    private readonly TerminalService _owner;
    private readonly Hex1bTerminalBuilder _builder;
    private readonly int _columns;
    private readonly int _rows;
    private readonly ILogger _logger;

    private Hmp1PresentationAdapter? _presentation;
    private Hex1bTerminal? _terminal;
    private Hex1bTerminalAutomator? _automator;
    private Task? _runTask;
    private Task? _stopTask;
    private bool _stopped;

    public Hex1bAspireTerminal(TerminalService owner, string id, string title, TerminalPlacement placement, Hex1bTerminalBuilder builder, int columns, int rows, ILogger logger)
    {
        _owner = owner;
        _builder = builder;
        _columns = columns;
        _rows = rows;
        _logger = logger;
        Id = id;
        Title = title;
        Placement = placement;
        Handle = new(this);
    }

    // Creation and lookup must return the same handle because interaction validation checks instance identity.
    public AspireTerminal Handle { get; }

    public string Id { get; }

    public string Title { get; private set; }

    public TerminalOwner Owner => TerminalOwner.AppHost;

    public TerminalPlacement Placement { get; }

    public TerminalDescriptor Descriptor => new(Id, Title);

    internal Task WorkloadEnded => _workloadEnded.Task;

    public void Start() => EnsureStarted();

    public void Show()
    {
        if (Placement != TerminalPlacement.Dock)
        {
            // A terminal in a dialog is revealed by that dialog, and one with no placement is not displayed
            // at all, so in neither case is there a dock tab to switch to.
            return;
        }

        _owner.NotifyActivated(this);
    }

    public void Retitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        lock (_gate)
        {
            if (string.Equals(Title, title, StringComparison.Ordinal))
            {
                return;
            }

            Title = title;
        }

        _owner.NotifyRetitled(this);
    }

    /// <summary>
    /// Attaches a viewer, starting the workload if this is the first thing to need it.
    /// </summary>
    /// <returns>
    /// A task that completes once this viewer disconnects or the terminal ends, and all operations on the
    /// caller's transport have finished. Callers keep their transport open until it completes.
    /// </returns>
    public async Task AttachAsync(Stream clientStream, Func<CancellationToken, Task> onEnded, CancellationToken cancellationToken)
    {
        // Hex1b owns and disposes the wrapper, never the gRPC stream. Closing it cancels only this viewer's
        // I/O and waits for outstanding accesses, even when Hex1b's other pump is still winding down.
        var attachment = new TerminalClientStream(clientStream);
        await using var _ = attachment.ConfigureAwait(false);
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        var lifetimeEnded = Task.WhenAny(_workloadEnded.Task, attachment.Released, cancelled.Task);
        var clientTask = Task.CompletedTask;
        lock (_gate)
        {
            if (!_workloadEnded.Task.IsCompleted)
            {
                EnsureStarted();
                clientTask = RunClientAsync(_presentation!, attachment, lifetimeEnded, clientCts.Token, _workloadCts.Token);
                _clientTasks.Add(clientTask);
            }
        }

        try
        {
            await lifetimeEnded.ConfigureAwait(false);
            if (_workloadEnded.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await onEnded(cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAny(_sessionEnded.Task, attachment.Released, cancelled.Task).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await clientCts.CancelAsync().ConfigureAwait(false);
                await clientTask.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _clientTasks.Remove(clientTask);
                }
            }
        }
    }

    private async Task RunClientAsync(
        Hmp1PresentationAdapter presentation,
        TerminalClientStream attachment,
        Task lifetimeEnded,
        CancellationToken clientCancellationToken,
        CancellationToken workloadCancellationToken)
    {
        // Keep handshake I/O off the thread holding _gate. Task registration and shutdown's snapshot
        // share that lock, so a stalled viewer neither blocks other viewers nor escapes cleanup.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(clientCancellationToken, workloadCancellationToken);
        await Task.Yield();

        try
        {
            var client = await presentation.AddClient(attachment, cts.Token).ConfigureAwait(false);
            await using var _ = client.ConfigureAwait(false);
            await lifetimeEnded.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Terminal {TerminalId} viewer connection ended.", Id);
        }
        finally
        {
            // AddClient can fail before Hex1b owns the stream (for example before ClientHello). Always
            // release our wrapper, but leave the underlying gRPC transport with its caller.
            await attachment.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the workload if it is not already running.
    /// </summary>
    /// <remarks>
    /// Callers start a terminal explicitly through <see cref="Start"/>. This remains the backstop for the two
    /// paths that cannot function without a running workload — a viewer attaching and an automation call — so
    /// that forgetting to start is a late start rather than a hard failure.
    /// </remarks>
    private Hex1bTerminal EnsureStarted()
    {
        lock (_gate)
        {
            if (_stopped || _workloadEnded.Task.IsCompleted)
            {
                throw new InvalidOperationException($"Terminal '{Id}' has already stopped.");
            }

            if (_terminal is not null)
            {
                return _terminal;
            }

            // WithHmp1Server creates an 80x24 adapter regardless of WithDimensions. Supply the adapter
            // directly so the PTY starts at the requested size, before any viewer can resize it.
            // Revisit the manual client wiring when https://github.com/mitchdenny/hex1b/issues/548 is fixed.
            _presentation = new Hmp1PresentationAdapter(_columns, _rows);
            _terminal = _builder
                .WithDimensions(_columns, _rows)
                .WithPresentation(_presentation)
                .WithReflow(GhosttyReflowStrategy.Instance)
                .WithScrollback(10000)
                .Build();

            _automator = new Hex1bTerminalAutomator(_terminal, TerminalAutomation.DefaultTimeout);

            _logger.LogDebug("Starting terminal {TerminalId} ({Title}).", Id, Title);

            _runTask = RunTerminalAsync(_terminal);
            return _terminal;
        }
    }

    private async Task RunTerminalAsync(Hex1bTerminal terminal)
    {
        // Yield before touching the terminal so RunAsync never executes inline under _gate.
        await Task.Yield();

        try
        {
            await terminal.RunAsync(_workloadCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the terminal is disposed while the workload is still running. Logged so the lifecycle
            // reads end-to-end alongside the "Starting terminal" entry above -- otherwise a cancelled workload is
            // indistinguishable from one that is still running.
            _logger.LogDebug("Terminal {TerminalId} ({Title}) workload was cancelled because the terminal is being disposed.", Id, Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal {TerminalId} ({Title}) failed unexpectedly and its session has ended.", Id, Title);
        }
        finally
        {
            Task[] clients;
            lock (_gate)
            {
                // Hex1b cannot serve completion to later HMP clients. Keep Aspire's registry entry (and dock tab),
                // but report completion ourselves rather than attaching to the disposed terminal.
                // Replace the separate notification when native ended-session support is available:
                // https://github.com/mitchdenny/hex1b/issues/483.
                _workloadEnded.TrySetResult();
                clients = [.. _clientTasks];
            }

            // Complete _sessionEnded only after disposal. Unlike _workloadEnded's UI notification, this signal
            // releases attached clients; Hex1b may still write to their transports during teardown.
            try
            {
                try
                {
                    // Natural process exit must also cancel handshakes that have not sent ClientHello;
                    // those streams are not yet owned by the presentation adapter.
                    await _workloadCts.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(clients).ConfigureAwait(false);
                }
                finally
                {
                    await terminal.DisposeAsync().ConfigureAwait(false);
                }
                _sessionEnded.TrySetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disposing terminal {TerminalId} ({Title}) failed.", Id, Title);
                _sessionEnded.TrySetException(ex);
            }
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        EnsureStarted();
        await TerminalAutomation.SendTextAsync(_automator!, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default)
    {
        var terminal = EnsureStarted();
        await TerminalAutomation.SendKeyAsync(terminal, _automator!, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        EnsureStarted();
        await TerminalAutomation.WaitForTextAsync(_automator!, Id, text, timeout, cancellationToken).ConfigureAwait(false);
    }

    public string GetScreenText()
    {
        lock (_gate)
        {
            if (_stopped || _workloadEnded.Task.IsCompleted)
            {
                throw new InvalidOperationException($"Terminal '{Id}' has already stopped.");
            }

            return TerminalAutomation.GetScreenText(_automator);
        }
    }

    /// <summary>
    /// Stops the workload without notifying the owning service. Used when the service is tearing everything down.
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _stopped = true;
            if (_runTask is null)
            {
                // Registered but never started, so there is nothing to wind down.
                _workloadCts.Dispose();
                _workloadEnded.TrySetResult();
                _sessionEnded.TrySetResult();
                return _stopTask = _sessionEnded.Task;
            }

            return _stopTask = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        // Cancellation callbacks must not run under _gate or prevent other terminals from beginning shutdown.
        await Task.Yield();

        try
        {
            // Cancellation interrupts Hex1b's process-exit wait. Its PTY disposal forcibly terminates any
            // remaining child, so join that disposal rather than relying on the AppHost process exiting.
            await Task.WhenAll(_workloadCts.CancelAsync(), _sessionEnded.Task).ConfigureAwait(false);
        }
        finally
        {
            _workloadCts.Dispose();
        }
    }

    public ValueTask DisposeAsync() => _owner.DisposeTerminalAsync(this);
}
