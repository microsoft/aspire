// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hex1b;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Owns every terminal whose process is hosted by the AppHost itself.
/// </summary>
/// <remarks>
/// <para>
/// Two experiences share this service: terminals belonging to an <see cref="InputType.Terminal"/> interaction
/// input, and terminals shown as tabs in the dashboard's terminal dock. They differ only in
/// <see cref="TerminalSurface"/>; the lifetime, transport, and automation machinery is identical.
/// </para>
/// <para>
/// This is distinct from the terminal host, which exists solely to surface terminals for DCP-owned processes.
/// Those are owned by the resource, reachable over a Unix domain socket, and are not tracked here.
/// </para>
/// <para>
/// Resolve it from the AppHost's service provider:
/// <c>builder.Services.GetRequiredService&lt;TerminalService&gt;()</c>. Only creation and lookup are public;
/// the members the dashboard uses to attach transports and watch the dock's tab list are internal, because
/// they are transport plumbing rather than something an AppHost author calls.
/// </para>
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class TerminalService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Hex1bAspireTerminal> _terminals = new(StringComparer.Ordinal);
    private readonly ILogger<TerminalService> _logger;
    private readonly object _syncLock = new();
    private ImmutableHashSet<Channel<TerminalChange>> _outgoingChannels = [];
    private int _disposed;

    internal TerminalService(ILogger<TerminalService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a terminal. The workload does not start until something needs it: the first viewer attaching,
    /// or the first automation call.
    /// </summary>
    /// <param name="options">Describes the terminal to create.</param>
    /// <returns>
    /// The terminal. Disposing it cancels the workload and removes the terminal from the dashboard; a dock
    /// terminal that is meant to outlive the call that created it should be left undisposed, and is torn down
    /// when the AppHost shuts down.
    /// </returns>
    public IAspireTerminal CreateTerminal(TerminalLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Command);

        return CreateTerminal(options.Title, options.Surface, CreateBuilder(options.Command));
    }

    /// <summary>
    /// Translates Aspire's terminal description into a configured Hex1b builder.
    /// </summary>
    /// <remarks>
    /// This is the single point where Hex1b enters the picture, which is what keeps it out of the public API.
    /// The process options overload is used rather than <c>WithPtyProcess(file, args)</c> so the working
    /// directory and environment can be set; <c>InheritEnvironment</c> is left at its default of
    /// <see langword="true"/>, so <see cref="TerminalCommand.EnvironmentVariables"/> layers over the AppHost's
    /// environment rather than replacing it. Interactive workloads need an inherited PATH/HOME/TERM to behave
    /// like a normal shell.
    /// </remarks>
    private static Hex1bTerminalBuilder CreateBuilder(TerminalCommand command)
    {
        return Hex1bTerminal.CreateBuilder()
            .WithDimensions(command.Columns, command.Rows)
            .WithPtyProcess(process =>
            {
                process.FileName = command.Executable;
                process.Arguments = [.. command.Arguments];
                process.WorkingDirectory = command.WorkingDirectory;

                if (command.EnvironmentVariables.Count > 0)
                {
                    process.Environment = new Dictionary<string, string>(command.EnvironmentVariables, StringComparer.Ordinal);
                }
            });
    }

    /// <summary>
    /// Creates a terminal from an already-configured Hex1b builder.
    /// </summary>
    /// <remarks>
    /// Internal because the builder is a Hex1b type. This is the path used by workloads that a
    /// <see cref="TerminalCommand"/> cannot describe — notably the dock's built-in terminal, which runs an
    /// in-process Hex1b app rather than a child process.
    /// </remarks>
    internal IAspireTerminal CreateTerminal(string title, TerminalSurface surface, Hex1bTerminalBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(builder);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // Terminal ids are opaque to the dashboard and appear in websocket query strings, so use a
        // non-guessable value rather than a sequence number.
        var id = Guid.NewGuid().ToString("n");
        var terminal = new Hex1bAspireTerminal(this, id, title, surface, builder, _logger);

        _terminals[id] = terminal;
        _logger.LogDebug("Created {Surface} terminal {TerminalId} ({Title}).", surface, id, title);

        if (terminal.Surface == TerminalSurface.Dock)
        {
            Publish(new TerminalChange(TerminalChangeType.Added, terminal.Descriptor));
        }

        return terminal;
    }

    /// <summary>
    /// Attaches a viewer transport to a terminal.
    /// </summary>
    /// <returns>
    /// A task that completes when the terminal ends or <paramref name="cancellationToken"/> is signalled.
    /// Callers keep their transport open until it completes.
    /// </returns>
    internal Task AttachAsync(string terminalId, Stream clientStream, CancellationToken cancellationToken)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal))
        {
            throw new InvalidOperationException($"There is no terminal with id '{terminalId}'.");
        }

        return terminal.AttachAsync(clientStream, cancellationToken);
    }

    /// <summary>
    /// Gets a terminal by id.
    /// </summary>
    /// <param name="terminalId">The <see cref="IAspireTerminal.Id"/> of the terminal to find.</param>
    /// <param name="terminal">The terminal, if one with that id exists.</param>
    /// <returns><see langword="true"/> if the terminal was found.</returns>
    public bool TryGetTerminal(string terminalId, [NotNullWhen(true)] out IAspireTerminal? terminal)
    {
        if (_terminals.TryGetValue(terminalId, out var found))
        {
            terminal = found;
            return true;
        }

        terminal = null;
        return false;
    }

    /// <summary>
    /// Subscribes to the dock's terminal list, returning the current set followed by a stream of changes.
    /// </summary>
    /// <remarks>
    /// The snapshot and the subscription are produced under the same lock so a terminal created concurrently
    /// is either in the snapshot or in the change stream, never dropped and never duplicated.
    /// </remarks>
    internal TerminalSubscription SubscribeDockTerminals()
    {
        lock (_syncLock)
        {
            var channel = Channel.CreateUnbounded<TerminalChange>(
                new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true, SingleWriter = false });

            ImmutableInterlocked.Update(ref _outgoingChannels, static (set, c) => set.Add(c), channel);

            var initial = _terminals.Values
                .Where(t => t.Surface == TerminalSurface.Dock)
                .Select(t => t.Descriptor)
                .ToImmutableArray();

            return new TerminalSubscription(initial, StreamChanges())
            {
                // The channel is registered above, before the caller has a chance to enumerate. StreamChanges is an
                // async iterator, so its finally only runs once someone calls MoveNextAsync -- a caller that faults
                // before it starts enumerating would otherwise leave the channel registered forever, and because it
                // is unbounded every later change would accumulate in it. Unsubscribe gives callers a deterministic
                // way to release the registration on that path. Removing twice is harmless.
                Unsubscribe = () => ImmutableInterlocked.Update(ref _outgoingChannels, static (set, c) => set.Remove(c), channel)
            };

            async IAsyncEnumerable<TerminalChange> StreamChanges([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield return change;
                    }
                }
                finally
                {
                    ImmutableInterlocked.Update(ref _outgoingChannels, static (set, c) => set.Remove(c), channel);
                }
            }
        }
    }

    internal void NotifyActivated(Hex1bAspireTerminal terminal)
        => Publish(new TerminalChange(TerminalChangeType.Activated, terminal.Descriptor));

    /// <summary>
    /// Removes a terminal from the registry and tears its workload down without waiting for it.
    /// </summary>
    /// <remarks>
    /// Used on the interaction completion path, which runs under a lock held by the interaction collection and
    /// must not block on a workload that may be ignoring cancellation. The registry entry is removed
    /// synchronously so the terminal is unreachable the moment the dialog closes.
    /// </remarks>
    internal void RemoveAndDisposeInBackground(string terminalId)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal))
        {
            return;
        }

        Remove(terminal);
        _ = DisposeQuietlyAsync(terminal);

        async Task DisposeQuietlyAsync(Hex1bAspireTerminal target)
        {
            try
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing terminal {TerminalId}.", target.Id);
            }
        }
    }

    internal void NotifyRetitled(Hex1bAspireTerminal terminal)
        => Publish(new TerminalChange(TerminalChangeType.Retitled, terminal.Descriptor));

    internal void Remove(Hex1bAspireTerminal terminal)
    {
        if (!_terminals.TryRemove(terminal.Id, out _))
        {
            return;
        }

        _logger.LogDebug("Removed terminal {TerminalId} ({Title}).", terminal.Id, terminal.Title);

        if (terminal.Surface == TerminalSurface.Dock)
        {
            Publish(new TerminalChange(TerminalChangeType.Removed, terminal.Descriptor));
        }
    }

    private void Publish(TerminalChange change)
    {
        foreach (var channel in _outgoingChannels)
        {
            channel.Writer.TryWrite(change);
        }
    }

    /// <summary>
    /// Tears down every terminal this service owns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var terminal in _terminals.Values)
        {
            Remove(terminal);

            // Don't await the workload winding down. AppHost shutdown should not be held up by a terminal
            // whose process ignores cancellation; the process is torn down with the AppHost regardless.
            _ = terminal.StopAsync();
        }

        foreach (var channel in _outgoingChannels)
        {
            channel.Writer.TryComplete();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// The current set of dock terminals plus a stream of subsequent changes.
/// </summary>
/// <remarks>
/// Dispose when the subscription is no longer needed. Enumerating <see cref="Subscription"/> to completion also
/// releases the registration, so disposing only matters on paths that abandon the subscription without ever
/// starting to enumerate it.
/// </remarks>
internal sealed record TerminalSubscription(
    ImmutableArray<TerminalDescriptor> InitialState,
    IAsyncEnumerable<TerminalChange> Subscription) : IDisposable
{
    /// <summary>
    /// Releases the change-stream registration held by this subscription. Safe to call more than once.
    /// </summary>
    public required Action Unsubscribe { get; init; }

    public void Dispose() => Unsubscribe();
}
