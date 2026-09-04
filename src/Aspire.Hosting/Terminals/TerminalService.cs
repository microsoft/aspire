// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

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
/// The service is internal for now. Making it public requires first replacing
/// <see cref="TerminalLaunchOptions.Builder"/> with an Aspire-shaped workload description, since that is the
/// only remaining place a Hex1b type is visible.
/// </para>
/// </remarks>
internal sealed class TerminalService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Hex1bAspireTerminal> _terminals = new(StringComparer.Ordinal);
    private readonly ILogger<TerminalService> _logger;
    private readonly IDockTerminalFactory _dockTerminalFactory;
    private readonly object _syncLock = new();
    private ImmutableHashSet<Channel<TerminalChange>> _outgoingChannels = [];
    private int _disposed;
    private int _dockTerminalCount;

    public TerminalService(ILogger<TerminalService> logger, IDockTerminalFactory dockTerminalFactory)
    {
        _logger = logger;
        _dockTerminalFactory = dockTerminalFactory;
    }

    /// <summary>
    /// Creates a terminal for the dashboard's terminal dock using the configured dock terminal factory.
    /// </summary>
    public IAspireTerminal CreateDockTerminal(string? title = null)
    {
        var options = _dockTerminalFactory.Create(title, Interlocked.Increment(ref _dockTerminalCount));
        options.Surface = TerminalSurface.Dock;
        return CreateTerminal(options);
    }

    /// <summary>
    /// Creates a terminal. The workload does not start until something needs it: the first viewer attaching,
    /// or the first automation call.
    /// </summary>
    public IAspireTerminal CreateTerminal(TerminalLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // Terminal ids are opaque to the dashboard and appear in websocket query strings, so use a
        // non-guessable value rather than a sequence number.
        var id = Guid.NewGuid().ToString("n");
        var terminal = new Hex1bAspireTerminal(this, id, options, _logger);

        _terminals[id] = terminal;
        _logger.LogDebug("Created {Surface} terminal {TerminalId} ({Title}).", options.Surface, id, options.Title);

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
    public Task AttachAsync(string terminalId, Stream clientStream, CancellationToken cancellationToken)
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
    public TerminalSubscription SubscribeDockTerminals()
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

            return new TerminalSubscription(initial, StreamChanges());

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
internal sealed record TerminalSubscription(
    ImmutableArray<TerminalDescriptor> InitialState,
    IAsyncEnumerable<TerminalChange> Subscription);
