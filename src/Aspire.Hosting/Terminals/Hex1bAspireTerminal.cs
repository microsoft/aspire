// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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
    private static readonly TimeSpan s_defaultAutomationTimeout = TimeSpan.FromSeconds(30);

    // Unbounded because the producer is a viewer attaching; the queue depth is realistically 0 or 1 and
    // dropping or blocking an attach would strand the RPC that is waiting to be served.
    private readonly Channel<Stream> _clients = Channel.CreateUnbounded<Stream>();

    // Two distinct signals, deliberately. _workloadCts stops the workload; _sessionEnded reports that
    // teardown has *finished*. Collapsing them into one token releases attached clients while Hex1b is
    // still disposing, which lets a gRPC handler return and dispose the transport out from under it.
    private readonly CancellationTokenSource _workloadCts = new();
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

    public Hex1bAspireTerminal(TerminalService owner, string id, string title, TerminalSurface surface, Hex1bTerminalBuilder builder, ILogger logger)
    {
        _owner = owner;
        _builder = builder;
        _logger = logger;
        Id = id;
        Title = title;
        Surface = surface;
    }

    public string Id { get; }

    public string Title { get; private set; }

    public TerminalSurface Surface { get; }

    public TerminalDescriptor Descriptor => new(Id, Title);

    public void Show()
    {
        if (Surface != TerminalSurface.Dock)
        {
            // Interaction terminals are revealed by their own dialog, so there is no dock tab to switch to.
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
    /// A task that completes once the terminal has fully torn down, or once <paramref name="cancellationToken"/>
    /// is signalled. Callers keep their transport open until it completes.
    /// </returns>
    public Task AttachAsync(Stream clientStream, CancellationToken cancellationToken)
    {
        EnsureStarted();

        if (!_clients.Writer.TryWrite(clientStream))
        {
            throw new InvalidOperationException($"Terminal '{Id}' is no longer accepting clients.");
        }

        return WaitForSessionEndAsync(cancellationToken);
    }

    private async Task WaitForSessionEndAsync(CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        await Task.WhenAny(_sessionEnded.Task, cancelled.Task).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the workload if it is not already running.
    /// </summary>
    /// <remarks>
    /// Startup is lazy so that an interaction dialog dismissed without ever opening its terminal never
    /// spawns a process. The first attach *or* the first automation call is what starts it.
    /// </remarks>
    private Hex1bTerminal EnsureStarted()
    {
        lock (_gate)
        {
            if (_stopped)
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

            _automator = new Hex1bTerminalAutomator(_terminal, s_defaultAutomationTimeout);

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
            // Expected when the terminal is disposed while the workload is still running.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal {TerminalId} ({Title}) failed.", Id, Title);
        }
        finally
        {
            // Dispose *before* releasing attached clients. Hex1b may still write to the attached transports
            // while it tears the terminal down; signalling completion first would let an attach caller return
            // and dispose its transport out from under Hex1b.
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
        await _automator!.TypeAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default)
    {
        var terminal = EnsureStarted();
        var sequence = AspireTerminalKeySequences.Get(key);
        await terminal.SendInputAsync(Encoding.UTF8.GetBytes(sequence), cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        EnsureStarted();

        // Hex1b's wait takes a timeout but no token, so the caller's cancellation is layered on here. The
        // underlying wait keeps running until its timeout elapses; that is acceptable because it is a passive
        // screen poll with no side effects.
        var wait = _automator!.WaitUntilTextAsync(text, timeout ?? s_defaultAutomationTimeout);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);

        var completed = await Task.WhenAny(wait, cancelled.Task).ConfigureAwait(false);
        if (completed != wait)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (WaitUntilTimeoutException ex)
        {
            // Translate so callers never have to reference Hex1b to handle a timeout.
            throw new TimeoutException($"Terminal '{Id}' did not display the expected text within the timeout.", ex);
        }
    }

    public string GetScreenText()
    {
        Hex1bTerminalAutomator? automator;
        lock (_gate)
        {
            automator = _automator;
        }

        // A terminal that has never been attached to or driven has no screen yet. Reporting empty is
        // friendlier than starting the workload as a side effect of a read.
        if (automator is null)
        {
            return string.Empty;
        }

        // The snapshot holds pooled buffers, so it must be released rather than left to finalization.
        using var snapshot = automator.CreateSnapshot();
        return snapshot.GetScreenText();
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
