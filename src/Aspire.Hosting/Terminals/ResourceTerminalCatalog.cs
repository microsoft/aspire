// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Discovers the terminals that belong to resources in the application model, and hands out
/// <see cref="IAspireTerminal"/> handles for them.
/// </summary>
/// <remarks>
/// <para>
/// Resource terminals are not registered with <see cref="TerminalService"/> the way AppHost terminals are.
/// They come and go with their replicas, and the AppHost is not their owner, so the application model plus the
/// per-replica terminal hosts remain the source of truth and this type projects that into the same shape as
/// the terminals the AppHost owns.
/// </para>
/// <para>
/// Handles are cached per replica so that repeated lookups share one automation connection rather than opening
/// a socket per call. They are created eagerly on lookup but connect lazily, so a handle that is listed and
/// never automated costs nothing.
/// </para>
/// </remarks>
internal sealed class ResourceTerminalCatalog : IAsyncDisposable
{
    /// <summary>
    /// Prefix distinguishing a resource terminal id from the opaque identifier of an AppHost terminal.
    /// </summary>
    /// <remarks>
    /// AppHost terminal ids are random and must stay unguessable because they appear in websocket query
    /// strings. A resource terminal is addressed by something the user already knows — the resource name and
    /// replica index — which is what lets <c>aspire terminal attach</c> and automation refer to the same
    /// terminal across a replica's terminal host being recycled.
    /// </remarks>
    public const string IdPrefix = "resource:";

    private readonly ConcurrentDictionary<string, ResourceAspireTerminal> _handles = new(StringComparer.Ordinal);
    private readonly DistributedApplicationModel _model;
    private readonly ILogger _logger;
    private int _disposed;

    public ResourceTerminalCatalog(DistributedApplicationModel model, ILogger logger)
    {
        _model = model;
        _logger = logger;
    }

    /// <summary>
    /// Builds the stable identifier for a resource terminal.
    /// </summary>
    public static string BuildId(string resourceName, int replicaIndex)
        => string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{resourceName}:{replicaIndex}");

    /// <summary>
    /// Determines whether an id addresses a resource terminal rather than an AppHost terminal.
    /// </summary>
    public static bool IsResourceTerminalId(string terminalId)
        => terminalId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Enumerates every terminal-enabled resource replica in the application model.
    /// </summary>
    /// <remarks>
    /// This reads only the application model, so it neither connects to a terminal host nor reports liveness.
    /// Callers that need per-replica health query the control socket separately; keeping the two apart is what
    /// lets a listing be produced without touching a socket.
    /// </remarks>
    public IReadOnlyList<ResourceTerminalEntry> List()
    {
        var entries = new List<ResourceTerminalEntry>();

        foreach (var resource in _model.Resources)
        {
            var annotation = resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault();
            if (annotation is null)
            {
                continue;
            }

            foreach (var host in annotation.TerminalHosts)
            {
                entries.Add(new ResourceTerminalEntry(
                    Id: BuildId(resource.Name, host.ParentReplicaIndex),
                    ResourceName: resource.Name,
                    ReplicaIndex: host.ParentReplicaIndex,
                    ReplicaCount: annotation.TerminalHosts.Count,
                    ConsumerUdsPath: host.Layout.ConsumerUdsPath,
                    ControlUdsPath: host.Layout.ControlUdsPath,
                    ConfiguredColumns: annotation.Options.Columns,
                    ConfiguredRows: annotation.Options.Rows));
            }
        }

        return entries;
    }

    /// <summary>
    /// Gets a handle for a resource terminal by its stable id.
    /// </summary>
    public bool TryGetTerminal(string terminalId, out IAspireTerminal? terminal)
    {
        terminal = null;

        if (_disposed != 0 || !IsResourceTerminalId(terminalId))
        {
            return false;
        }

        var entry = List().FirstOrDefault(e => string.Equals(e.Id, terminalId, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }

        terminal = _handles.GetOrAdd(
            entry.Id,
            static (id, state) => new ResourceAspireTerminal(id, state.Entry.Title, state.Entry.ConsumerUdsPath, state.Logger),
            (Entry: entry, Logger: _logger));

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var handle in _handles.Values)
        {
            // Disposing a resource terminal handle disconnects the AppHost's automation peer; the resource's
            // own workload is unaffected, so there is nothing here that should delay shutdown.
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disconnecting the automation peer for resource terminal {TerminalId} failed.", handle.Id);
            }
        }

        _handles.Clear();
    }
}

/// <summary>
/// One terminal-enabled resource replica, as described by the application model.
/// </summary>
internal sealed record ResourceTerminalEntry(
    string Id,
    string ResourceName,
    int ReplicaIndex,
    int ReplicaCount,
    string ConsumerUdsPath,
    string ControlUdsPath,
    int ConfiguredColumns,
    int ConfiguredRows)
{
    /// <summary>
    /// Gets the title shown for this terminal, qualified by replica only when the resource has more than one.
    /// </summary>
    public string Title => ReplicaCount > 1
        ? string.Create(CultureInfo.InvariantCulture, $"{ResourceName} (replica {ReplicaIndex})")
        : ResourceName;
}
