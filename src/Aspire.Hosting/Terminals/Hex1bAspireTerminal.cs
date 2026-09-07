// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Hex1b;
using Hex1b.Automation;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The Hex1b-backed implementation of <see cref="IAspireTerminal"/>.
/// </summary>
/// <remarks>
/// Clients are handed to Hex1b's HMP1 server through a channel, which lets a single terminal serve several
/// attached viewers (for example two dashboard browser tabs, or a dock tab reopened after being closed)
/// using HMP1's multi-head support. The workload lives in the AppHost, so terminal state survives a viewer
/// disconnecting entirely.
/// </remarks>
internal sealed class Hex1bAspireTerminal : IAspireTerminal
{
    // Unbounded because the producer is a viewer attaching; the queue depth is realistically 0 or 1 and
    // dropping or blocking an attach would strand the RPC that is waiting to be served.
    private readonly Channel<Stream> _clients = Channel.CreateUnbounded<Stream>();

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
    private readonly ILogger _logger;

    private Hex1bTerminal? _terminal;
    private Hex1bTerminalAutomator? _automator;
    private Task? _runTask;
    private bool _stopped;

    public Hex1bAspireTerminal(TerminalService owner, string id, string title, TerminalPlacement placement, Hex1bTerminalBuilder builder, ILogger logger)
    {
        _owner = owner;
        _builder = builder;
        _logger = logger;
        Id = id;
        Title = title;
        Placement = placement;
    }

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
        lock (_gate)
        {
            if (!_workloadEnded.Task.IsCompleted)
            {
                EnsureStarted();
                if (!_clients.Writer.TryWrite(attachment))
                {
                    throw new InvalidOperationException($"Terminal '{Id}' is no longer accepting clients.");
                }
            }
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        await Task.WhenAny(_workloadEnded.Task, attachment.Released, cancelled.Task).ConfigureAwait(false);
        if (_workloadEnded.Task.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await onEnded(cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAny(_sessionEnded.Task, attachment.Released, cancelled.Task).ConfigureAwait(false);
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

            // Aspire owns the transport: the caller configures only the workload, and the HMP1 server is
            // attached here so the terminal is reachable over the dashboard gRPC tunnel rather than a Unix
            // domain socket.
            _terminal = _builder
                .WithHmp1Server(_clients.Reader.ReadAllAsync)
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
            lock (_gate)
            {
                // Hex1b cannot serve completion to later HMP clients. Keep Aspire's registry entry (and dock tab),
                // but report completion ourselves rather than attaching to the disposed terminal.
                // Replace the separate notification when native ended-session support is available:
                // https://github.com/mitchdenny/hex1b/issues/483.
                _clients.Writer.TryComplete();
                _workloadEnded.TrySetResult();
            }

            // Complete _sessionEnded only after disposal. Unlike _workloadEnded's UI notification, this signal
            // releases attached clients; Hex1b may still write to their transports during teardown.
            try
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing terminal {TerminalId} ({Title}) failed.", Id, Title);
            }

            _sessionEnded.TrySetResult();
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
        await TerminalAutomation.SendKeyAsync(terminal, key, cancellationToken).ConfigureAwait(false);
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
            if (_stopped)
            {
                return _sessionEnded.Task;
            }

            _stopped = true;
            _clients.Writer.TryComplete();

            if (_runTask is null)
            {
                // Registered but never started, so there is nothing to wind down.
                _workloadCts.Cancel();
                _workloadCts.Dispose();
                _workloadEnded.TrySetResult();
                _sessionEnded.TrySetResult();
                return _sessionEnded.Task;
            }
        }

        _workloadCts.Cancel();

        _ = _sessionEnded.Task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _workloadCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return _sessionEnded.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Remove(this);
        await StopAsync().ConfigureAwait(false);
    }
}
