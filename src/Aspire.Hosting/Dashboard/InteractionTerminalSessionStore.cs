// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Hex1b;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Owns the lifetime of terminal sessions created for <see cref="InputType.Terminal"/> interaction inputs.
/// </summary>
internal sealed class InteractionTerminalSessionStore : IInteractionTerminalSessionStore, IDisposable
{
    private readonly ConcurrentDictionary<int, TerminalInteraction> _interactions = new();
    private readonly ILogger<InteractionTerminalSessionStore> _logger;
    private int _disposed;

    public InteractionTerminalSessionStore(ILogger<InteractionTerminalSessionStore> logger)
    {
        _logger = logger;
    }

    public void StartInteraction(int interactionId, IReadOnlyList<(string InputName, Hex1bTerminalBuilder Builder)> terminalInputs)
    {
        var sessions = new Dictionary<string, TerminalSession>(StringComparers.InteractionInputName);
        foreach (var (inputName, builder) in terminalInputs)
        {
            sessions[inputName] = new TerminalSession(interactionId, inputName, builder, _logger);
        }

        if (_interactions.TryAdd(interactionId, new TerminalInteraction(sessions)))
        {
            _logger.LogDebug(
                "Started tracking {SessionCount} terminal session(s) for interaction {InteractionId}.",
                sessions.Count,
                interactionId);
        }
    }

    public Task AttachAsync(int interactionId, string inputName, Stream clientStream, CancellationToken cancellationToken)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction) ||
            !interaction.Sessions.TryGetValue(inputName, out var session))
        {
            throw new InvalidOperationException($"Interaction '{interactionId}' does not have a terminal input named '{inputName}'.");
        }

        return session.AttachAsync(clientStream, cancellationToken);
    }

    public void CompleteInteraction(int interactionId) => EndInteraction(interactionId, "completed");

    public void CancelInteraction(int interactionId) => EndInteraction(interactionId, "cancelled");

    private void EndInteraction(int interactionId, string reason)
    {
        if (!_interactions.TryRemove(interactionId, out var interaction))
        {
            return;
        }

        _logger.LogDebug(
            "Tearing down {SessionCount} terminal session(s) for {Reason} interaction {InteractionId}.",
            interaction.Sessions.Count,
            reason,
            interactionId);

        foreach (var session in interaction.Sessions.Values)
        {
            session.Stop();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var interactionId in _interactions.Keys)
        {
            EndInteraction(interactionId, "disposed");
        }
    }

    private sealed class TerminalInteraction(Dictionary<string, TerminalSession> sessions)
    {
        public Dictionary<string, TerminalSession> Sessions { get; } = sessions;
    }

    /// <summary>
    /// A single AppHost-owned terminal. Clients are handed to Hex1b's HMP1 server through a channel, which lets the
    /// same session serve several attached viewers (for example two dashboard tabs) using HMP1's multi-head support.
    /// </summary>
    private sealed class TerminalSession(int interactionId, string inputName, Hex1bTerminalBuilder builder, ILogger logger)
    {
        // Unbounded because the producer is a human attaching a viewer; the queue depth is realistically 0 or 1 and
        // dropping or blocking an attach would strand the RPC that is waiting to be served.
        private readonly Channel<Stream> _clients = Channel.CreateUnbounded<Stream>();
        private readonly CancellationTokenSource _stopCts = new();
        // Aspire.Hosting targets net8.0, which predates System.Threading.Lock, so this is a plain monitor gate.
        private readonly object _gate = new();
        private Hex1bTerminal? _terminal;
        private Task? _runTask;
        private bool _stopped;

        public Task AttachAsync(Stream clientStream, CancellationToken cancellationToken)
        {
            EnsureStarted();

            if (!_clients.Writer.TryWrite(clientStream))
            {
                throw new InvalidOperationException($"Terminal session for input '{inputName}' is no longer accepting clients.");
            }

            // The caller's transport must stay open for as long as Hex1b may use the stream. The session's own token
            // ends the wait when the interaction is torn down, which lets the transport close from the AppHost side
            // instead of lingering until the user closes the browser.
            return WaitForSessionEndAsync(cancellationToken);
        }

        private async Task WaitForSessionEndAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopCts.Token);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = linked.Token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), completion);
            await completion.Task.ConfigureAwait(false);
        }

        private void EnsureStarted()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    throw new InvalidOperationException($"Terminal session for input '{inputName}' has already stopped.");
                }

                if (_terminal is not null)
                {
                    return;
                }

                // Aspire owns the transport: the caller configures only the workload, and the HMP1 server is attached
                // here so the session is reachable over the dashboard gRPC tunnel rather than a Unix domain socket.
                // Started lazily so a dialog dismissed without opening the terminal never spawns the workload.
                _terminal = builder
                    .WithHmp1Server(_clients.Reader.ReadAllAsync)
                    .Build();

                logger.LogDebug(
                    "Starting terminal session for interaction {InteractionId}, input {InputName}.",
                    interactionId,
                    inputName);

                _runTask = RunTerminalAsync(_terminal);
            }
        }

        private async Task RunTerminalAsync(Hex1bTerminal terminal)
        {
            // Yield before touching the terminal so RunAsync never executes inline under _gate.
            await Task.Yield();

            try
            {
                await terminal.RunAsync(_stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the interaction completes while the terminal is still running.
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Terminal session for interaction {InteractionId}, input {InputName} failed.",
                    interactionId,
                    inputName);
            }
            finally
            {
                // Unblock every attached client so their transports close rather than waiting for the interaction to
                // end. This is the path taken when the workload itself exits, e.g. the user types `exit`.
                _stopCts.Cancel();
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void Stop()
        {
            Task? runTask;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _clients.Writer.TryComplete();
                runTask = _runTask;
            }

            _stopCts.Cancel();

            if (runTask is null)
            {
                // The session was registered but never attached to, so there is nothing to wind down and no terminal
                // was ever built. Dispose the token source directly.
                _stopCts.Dispose();
                return;
            }

            // Don't block interaction teardown on the workload exiting; dispose the token source once it has.
            _ = runTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _stopCts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
