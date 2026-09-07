// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hex1b;
using Microsoft.Extensions.Configuration;
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
/// <see cref="TerminalPlacement"/>; the lifetime, transport, and automation machinery is identical.
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
/// <para>
/// Each dashboard metadata watcher buffers up to 64 updates by default. Set
/// <c>ASPIRE_TERMINAL_WATCH_BUFFER_CAPACITY</c> to a positive integer in the AppHost's configuration
/// before starting the application to tune this limit. Overflow replaces queued changes with a current
/// snapshot while preserving the latest pending request to show the dock.
/// </para>
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class TerminalService : IAsyncDisposable
{
    internal const int DefaultDockUpdateBufferCapacity = 64;

    private readonly ConcurrentDictionary<string, Hex1bAspireTerminal> _terminals = new(StringComparer.Ordinal);
    private readonly ILogger<TerminalService> _logger;
    private readonly int _dockUpdateBufferCapacity;
    private readonly object _syncLock = new();
    private ImmutableHashSet<Channel<TerminalUpdate>> _outgoingChannels = [];
    private int _disposed;

    internal TerminalService(ILogger<TerminalService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _dockUpdateBufferCapacity = configuration.GetValue(KnownConfigNames.TerminalWatchBufferCapacity, DefaultDockUpdateBufferCapacity);
        if (_dockUpdateBufferCapacity <= 0)
        {
            throw new InvalidOperationException($"Configuration '{KnownConfigNames.TerminalWatchBufferCapacity}' must be greater than zero.");
        }
    }

    /// <summary>
    /// Gets or sets the catalog consulted for terminals that belong to resources rather than to the AppHost.
    /// </summary>
    /// <remarks>
    /// Assigned once the application model exists. It is settable rather than a constructor argument because
    /// this service is created before the model is built, and it stays null in tests that exercise only
    /// AppHost terminals.
    /// </remarks>
    internal ResourceTerminalCatalog? ResourceTerminals { get; set; }

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The placement in <paramref name="options"/> is not <see cref="TerminalPlacement.Dock"/>,
    /// <see cref="TerminalPlacement.Dialog"/>, or <see cref="TerminalPlacement.None"/>.
    /// </exception>
    public IAspireTerminal CreateTerminal(TerminalLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Command);

        return CreateTerminal(options.Title, options.Placement, CreateBuilder(options.Command));
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
    internal IAspireTerminal CreateTerminal(string title, TerminalPlacement placement, Hex1bTerminalBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(builder);

        // AppHost-owned terminals have no resource view. Validate both creation paths here before registration,
        // while retaining None for terminals driven only through automation.
        if (placement is not (TerminalPlacement.Dock or TerminalPlacement.Dialog or TerminalPlacement.None))
        {
            throw new ArgumentOutOfRangeException(nameof(placement), placement,
                $"AppHost-owned terminals must use {nameof(TerminalPlacement.Dock)}, {nameof(TerminalPlacement.Dialog)}, or {nameof(TerminalPlacement.None)} placement.");
        }

        // Terminal ids are opaque to the dashboard and appear in websocket query strings, so use a
        // non-guessable value rather than a sequence number.
        var id = Guid.NewGuid().ToString("n");
        Hex1bAspireTerminal terminal;

        // Registration, publication, snapshots, and shutdown share one boundary. Otherwise a subscriber
        // can see both a snapshot entry and its Added event, or shutdown can miss a new registration.
        lock (_syncLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            terminal = new Hex1bAspireTerminal(this, id, title, placement, builder, _logger);
            _terminals[id] = terminal;

            if (terminal.Placement == TerminalPlacement.Dock)
            {
                Publish(new TerminalChange(TerminalChangeType.Added, terminal.Descriptor));
            }
        }

        _logger.LogDebug("Created {Placement} terminal {TerminalId} ({Title}).", placement, id, title);

        return terminal;
    }

    /// <summary>
    /// Attaches a viewer transport to a terminal.
    /// </summary>
    /// <returns>
    /// A task that completes after this viewer disconnects or the terminal ends, once all operations on the
    /// caller's transport have finished. Cancellation disconnects only this viewer.
    /// </returns>
    internal Task AttachAsync(string terminalId, Stream clientStream, Func<CancellationToken, Task> onEnded, CancellationToken cancellationToken)
    {
        if (!_terminals.TryGetValue(terminalId, out var terminal))
        {
            throw new InvalidOperationException($"There is no terminal with id '{terminalId}'.");
        }

        return terminal.AttachAsync(clientStream, onEnded, cancellationToken);
    }

    /// <summary>
    /// Gets a terminal by id.
    /// </summary>
    /// <param name="terminalId">The <see cref="IAspireTerminal.Id"/> of the terminal to find.</param>
    /// <param name="terminal">The terminal, if one with that id exists.</param>
    /// <returns><see langword="true"/> if the terminal was found.</returns>
    /// <remarks>
    /// Resolves terminals the AppHost owns as well as those belonging to resources, so automation code can
    /// drive either kind through the same handle without knowing which it has.
    /// </remarks>
    public bool TryGetTerminal(string terminalId, [NotNullWhen(true)] out IAspireTerminal? terminal)
    {
        ArgumentNullException.ThrowIfNull(terminalId);

        if (_terminals.TryGetValue(terminalId, out var found))
        {
            terminal = found;
            return true;
        }

        if (ResourceTerminals?.TryGetTerminal(terminalId, out var resourceTerminal) == true)
        {
            terminal = resourceTerminal!;
            return true;
        }

        terminal = null;
        return false;
    }

    /// <summary>
    /// Lists every terminal in the AppHost, whether the AppHost or a resource owns it.
    /// </summary>
    /// <remarks>
    /// This is what makes a single listing possible: dock tabs, terminals being shown in an interaction
    /// dialog, terminals driven only by automation, and each terminal-enabled resource replica all appear
    /// here. Resource entries are read from the application model and carry no liveness — obtaining that
    /// requires a round trip to each replica's terminal host, which a listing should not force on callers
    /// that only want to know what exists.
    /// </remarks>
    internal IReadOnlyList<TerminalListing> ListAll()
    {
        var listings = new List<TerminalListing>();

        foreach (var terminal in _terminals.Values)
        {
            listings.Add(new TerminalListing(
                terminal.Id,
                terminal.Title,
                TerminalOwner.AppHost,
                terminal.Placement,
                ResourceName: null,
                ReplicaIndex: null,
                ConsumerUdsPath: null,
                ControlUdsPath: null));
        }

        foreach (var entry in ResourceTerminals?.List() ?? [])
        {
            listings.Add(new TerminalListing(
                entry.Id,
                entry.Title,
                TerminalOwner.Resource,
                TerminalPlacement.ResourceView,
                entry.ResourceName,
                entry.ReplicaIndex,
                entry.ConsumerUdsPath,
                entry.ControlUdsPath));
        }

        return listings;
    }

    /// <summary>
    /// Subscribes to the dock's terminal list, returning the current set followed by changes or recovery snapshots.
    /// </summary>
    /// <remarks>
    /// The snapshot and the subscription are produced under the same lock so a terminal created concurrently
    /// is either in the snapshot or in the change stream, never dropped and never duplicated.
    /// </remarks>
    internal TerminalSubscription SubscribeDockTerminals()
    {
        lock (_syncLock)
        {
            var channel = Channel.CreateBounded<TerminalUpdate>(new BoundedChannelOptions(_dockUpdateBufferCapacity)
            {
                AllowSynchronousContinuations = false,
                // Publish also drains a full queue before replacing it with a snapshot.
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            if (_disposed != 0)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                ImmutableInterlocked.Update(ref _outgoingChannels, static (set, c) => set.Add(c), channel);
            }

            var initial = GetDockSnapshot();

            return new TerminalSubscription(initial, StreamChanges())
            {
                // The channel is registered above, before the caller has a chance to enumerate. StreamChanges is an
                // async iterator, so its finally only runs once someone calls MoveNextAsync. A caller that faults
                // during the initial snapshot write must still be able to release the channel and its buffer.
                Unsubscribe = () => Unsubscribe(channel)
            };

            async IAsyncEnumerable<TerminalUpdate> StreamChanges([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        TerminalUpdate? change;
                        lock (_syncLock)
                        {
                            // Do not let the reader take a newer activation between entries drained by Publish,
                            // then receive a recovery snapshot that replays an older activation.
                            channel.Reader.TryRead(out change);
                        }

                        if (change is not null)
                        {
                            yield return change;
                        }
                    }
                }
                finally
                {
                    Unsubscribe(channel);
                }
            }
        }
    }

    private ImmutableArray<TerminalDescriptor> GetDockSnapshot()
        => _terminals.Values
            .Where(t => t.Placement == TerminalPlacement.Dock)
            .Select(t => t.Descriptor)
            .ToImmutableArray();

    private void Unsubscribe(Channel<TerminalUpdate> channel)
    {
        lock (_syncLock)
        {
            ImmutableInterlocked.Update(ref _outgoingChannels, static (set, c) => set.Remove(c), channel);
            channel.Writer.TryComplete();
        }
    }

    internal void NotifyActivated(Hex1bAspireTerminal terminal)
        => Notify(terminal, TerminalChangeType.Activated);

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
        => Notify(terminal, TerminalChangeType.Retitled);

    private void Notify(Hex1bAspireTerminal terminal, TerminalChangeType changeType)
    {
        lock (_syncLock)
        {
            if (terminal.Placement == TerminalPlacement.Dock && _terminals.ContainsKey(terminal.Id))
            {
                Publish(new TerminalChange(changeType, terminal.Descriptor));
            }
        }
    }

    internal void Remove(Hex1bAspireTerminal terminal)
    {
        lock (_syncLock)
        {
            if (!_terminals.TryRemove(terminal.Id, out _))
            {
                return;
            }

            if (terminal.Placement == TerminalPlacement.Dock)
            {
                Publish(new TerminalChange(TerminalChangeType.Removed, terminal.Descriptor));
            }
        }

        _logger.LogDebug("Removed terminal {TerminalId} ({Title}).", terminal.Id, terminal.Title);
    }

    private void Publish(TerminalChange change)
    {
        ImmutableArray<TerminalDescriptor> snapshot = default;
        foreach (var channel in _outgoingChannels)
        {
            if (channel.Writer.TryWrite(change))
            {
                continue;
            }

            // All publishers, registration and completion share _syncLock. A slow gRPC writer must neither block
            // AppHost operations nor retain unlimited history. Replace its backlog with state captured under that
            // same lock, so later deltas always follow the snapshot they extend. Readers may still finish sending
            // an older dequeued update first; the replacement snapshot supersedes it.
            string? activatedTerminalId = null;
            while (channel.Reader.TryRead(out var pending))
            {
                activatedTerminalId = pending switch
                {
                    TerminalChange { ChangeType: TerminalChangeType.Activated } activation => activation.Terminal.Id,
                    TerminalSnapshot recovery => recovery.ActivatedTerminalId,
                    _ => activatedTerminalId
                };
            }

            if (change.ChangeType == TerminalChangeType.Activated)
            {
                activatedTerminalId = change.Terminal.Id;
            }

            // Share the immutable inventory when several subscribers overflow on the same publication.
            if (snapshot.IsDefault)
            {
                snapshot = GetDockSnapshot();
            }
            channel.Writer.TryWrite(new TerminalSnapshot(snapshot, activatedTerminalId));
        }
    }

    /// <summary>
    /// Tears down every terminal this service owns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Hex1bAspireTerminal[] terminals;
        lock (_syncLock)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            terminals = [.. _terminals.Values];
            _terminals.Clear();

            foreach (var terminal in terminals)
            {
                if (terminal.Placement == TerminalPlacement.Dock)
                {
                    Publish(new TerminalChange(TerminalChangeType.Removed, terminal.Descriptor));
                }
            }

            foreach (var channel in _outgoingChannels)
            {
                channel.Writer.TryComplete();
            }
        }

        foreach (var terminal in terminals)
        {
            _logger.LogDebug("Removed terminal {TerminalId} ({Title}).", terminal.Id, terminal.Title);

            // Don't await the workload winding down. AppHost shutdown should not be held up by a terminal
            // whose process ignores cancellation; the process is torn down with the AppHost regardless.
            _ = terminal.StopAsync();
        }

        if (ResourceTerminals is { } resourceTerminals)
        {
            await resourceTerminals.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The current set of dock terminals plus a stream of subsequent changes or recovery snapshots.
/// </summary>
/// <remarks>
/// Dispose when the subscription is no longer needed, including paths that abandon it before enumeration starts.
/// Disposal completes the stream, and enumerating <see cref="Subscription"/> to completion also releases its registration.
/// </remarks>
internal sealed record TerminalSubscription(
    ImmutableArray<TerminalDescriptor> InitialState,
    IAsyncEnumerable<TerminalUpdate> Subscription) : IDisposable
{
    /// <summary>
    /// Releases the change-stream registration held by this subscription. Safe to call more than once.
    /// </summary>
    public required Action Unsubscribe { get; init; }

    public void Dispose() => Unsubscribe();
}

/// <summary>
/// One terminal in the AppHost, as reported by <see cref="TerminalService.ListAll"/>.
/// </summary>
/// <remarks>
/// The resource-specific members are populated only for <see cref="TerminalOwner.Resource"/> terminals, whose
/// per-replica liveness is obtained by querying <see cref="ControlUdsPath"/>.
/// </remarks>
internal sealed record TerminalListing(
    string Id,
    string Title,
    TerminalOwner Owner,
    TerminalPlacement Placement,
    string? ResourceName,
    int? ReplicaIndex,
    string? ConsumerUdsPath,
    string? ControlUdsPath);
