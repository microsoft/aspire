// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Aspire.Shared.ConsoleLogs;
using Dapper;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Stores dashboard resources and console logs in SQLite.
/// </summary>
public sealed partial class SqliteResourceRepository : IResourceRepository, IResourceRepositoryWriter, IDisposable
{
    private readonly DashboardSqliteDatabase _database;
    private readonly IKnownPropertyLookup _knownPropertyLookup;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, ResourceState> _resourceStates = new(StringComparers.ResourceName);
    private ImmutableHashSet<Channel<IReadOnlyList<ResourceViewModelChange>>> _resourceChannels = [];
    private readonly Dictionary<string, ImmutableHashSet<Channel<IReadOnlyList<ResourceLogLine>>>> _consoleChannels = new(StringComparers.ResourceName);
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteResourceRepository"/> class.
    /// </summary>
    /// <param name="database">The dashboard database used to persist resources and console logs.</param>
    /// <param name="knownPropertyLookup">The lookup for known resource properties.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public SqliteResourceRepository(
        DashboardSqliteDatabase database,
        IKnownPropertyLookup knownPropertyLookup,
        ILoggerFactory loggerFactory)
    {
        _database = database;
        _knownPropertyLookup = knownPropertyLookup;
        _logger = loggerFactory.CreateLogger<SqliteResourceRepository>();

        LoadResources();
    }

    public ResourceViewModel? GetResource(string resourceName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            return _resourceStates.GetValueOrDefault(resourceName)?.Resource;
        }
    }

    public IReadOnlyList<ResourceViewModel> GetResources()
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            return _resourceStates.Values.Select(state => state.Resource).ToList();
        }
    }

    public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
        ImmutableArray<ResourceViewModel> initialState;
        lock (_stateLock)
        {
            _resourceChannels = _resourceChannels.Add(channel);
            initialState = _resourceStates.Values.Select(state => state.Resource).ToImmutableArray();
        }

        return Task.FromResult(new ResourceViewModelSubscription(
            initialState,
            ReadResourceUpdatesAsync(channel, cancellationToken)));
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(
        string resourceName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ResourceLogLine[] lines;
        ThrowIfDisposed();
        using var connection = _database.OpenConnection();
        lines = connection.Query<ConsoleLogRecord>("""
            SELECT line_number AS LineNumber, content AS Content, is_stderr AS IsStdErr
            FROM console_logs
            WHERE resource_name = @ResourceName
            ORDER BY console_log_id;
            """, new { ResourceName = resourceName })
            .Select(line => new ResourceLogLine(line.LineNumber, line.Content, line.IsStdErr))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        if (lines.Length > 0)
        {
            yield return lines;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(
        string resourceName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ResourceLogLine[] initialLines;
        Channel<IReadOnlyList<ResourceLogLine>> channel;
        ThrowIfDisposed();
        // Keep the initial query and channel registration atomic with console-log writes. Otherwise, a write
        // could commit after the query but before registration, causing the subscriber to miss those lines.
        using (await _database.WriteLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            using var connection = _database.OpenConnection();
            initialLines = connection.Query<ConsoleLogRecord>("""
                SELECT line_number AS LineNumber, content AS Content, is_stderr AS IsStdErr
                FROM console_logs
                WHERE resource_name = @ResourceName
                ORDER BY console_log_id;
                """, new { ResourceName = resourceName })
                .Select(line => new ResourceLogLine(line.LineNumber, line.Content, line.IsStdErr))
                .ToArray();

            channel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
            lock (_stateLock)
            {
                _consoleChannels[resourceName] = (_consoleChannels.GetValueOrDefault(resourceName) ?? []).Add(channel);
            }
        }

        try
        {
            if (initialLines.Length > 0)
            {
                yield return initialLines;
            }

            await foreach (var lines in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return lines;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (_consoleChannels.TryGetValue(resourceName, out var channels))
                {
                    channels = channels.Remove(channel);
                    if (channels.Count == 0)
                    {
                        _consoleChannels.Remove(resourceName);
                    }
                    else
                    {
                        _consoleChannels[resourceName] = channels;
                    }
                }
            }
        }
    }

    async Task IResourceRepositoryWriter.ReplaceResourcesAsync(IReadOnlyList<Resource> resources)
    {
        EnsureWritable();
        ThrowIfDisposed();
        List<ResourceViewModelChange> changes = [];

        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
        {
            Dictionary<string, ResourceViewModel> currentResources;
            lock (_stateLock)
            {
                currentResources = _resourceStates.ToDictionary(entry => entry.Key, entry => entry.Value.Resource, StringComparers.ResourceName);
            }

            var replacementResources = new Dictionary<string, ResourceViewModel>(StringComparers.ResourceName);
            var resourcesToSave = new List<ResourceToSave>(resources.Count);
            var replacementResourceNames = resources.Select(resource => resource.Name).ToHashSet(StringComparers.ResourceName);
            foreach (var (resourceName, viewModel) in currentResources)
            {
                if (!replacementResourceNames.Contains(resourceName))
                {
                    changes.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Delete, viewModel));
                }
            }
            var resourcesWithLoadedConsoleLogs = currentResources.Values
                .Where(resource => resource.ConsoleLogsLoaded)
                .Select(resource => resource.Name)
                .ToHashSet(StringComparers.ResourceName);

            foreach (var resource in resources)
            {
                var viewModel = CreateViewModel(resource, replacementResources);
                viewModel.ConsoleLogsLoaded = resourcesWithLoadedConsoleLogs.Contains(resource.Name);
                resourcesToSave.Add(new(resource, viewModel.ReplicaIndex, viewModel.ConsoleLogsLoaded));
                replacementResources[resource.Name] = viewModel;
                changes.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            connection.Execute("DELETE FROM dashboard_resources;", transaction: transaction);
            InsertResources(connection, transaction, resourcesToSave);
            transaction.Commit();

            lock (_stateLock)
            {
                UpdateResourceStates(replacementResources);
            }
        }

        PublishResourceChanges(changes);
    }

    async Task IResourceRepositoryWriter.ApplyChangesAsync(IReadOnlyList<WatchResourcesChange> changes)
    {
        EnsureWritable();
        ThrowIfDisposed();
        List<ResourceViewModelChange> viewModelChanges = [];

        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
        {
            var affectedResourceNames = changes
                .Select(change => change.KindCase switch
                {
                    WatchResourcesChange.KindOneofCase.Upsert => change.Upsert.Name,
                    WatchResourcesChange.KindOneofCase.Delete => change.Delete.ResourceName,
                    _ => null
                })
                .Where(name => name is not null)
                .Distinct(StringComparers.ResourceName)
                .ToArray();
            var resourcesToSave = new Dictionary<string, ResourceToSave>(StringComparers.ResourceName);
            Dictionary<string, ResourceViewModel> updatedResources;

            lock (_stateLock)
            {
                updatedResources = _resourceStates.ToDictionary(entry => entry.Key, entry => entry.Value.Resource, StringComparers.ResourceName);
            }
            foreach (var change in changes)
            {
                if (change.KindCase == WatchResourcesChange.KindOneofCase.Upsert)
                {
                    var resource = change.Upsert;
                    var viewModel = CreateViewModel(resource, updatedResources);
                    resourcesToSave[resource.Name] = new(resource, viewModel.ReplicaIndex, viewModel.ConsoleLogsLoaded);
                    updatedResources[resource.Name] = viewModel;
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
                }
                else if (change.KindCase == WatchResourcesChange.KindOneofCase.Delete && updatedResources.Remove(change.Delete.ResourceName, out var removed))
                {
                    resourcesToSave.Remove(change.Delete.ResourceName);
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Delete, removed));
                }
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            connection.Execute("DELETE FROM dashboard_resources WHERE resource_name IN @ResourceNames;", new { ResourceNames = affectedResourceNames }, transaction);
            InsertResources(connection, transaction, resourcesToSave.Values.ToArray());
            transaction.Commit();

            lock (_stateLock)
            {
                UpdateResourceStates(updatedResources);
            }
        }

        PublishResourceChanges(viewModelChanges);
    }

    async Task IResourceRepositoryWriter.MarkConsoleLogsLoadedAsync(string resourceName)
    {
        EnsureWritable();
        ThrowIfDisposed();
        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
        {
            using var connection = _database.OpenConnection();
            connection.Execute("""
                UPDATE dashboard_resources
                SET console_logs_loaded = 1
                WHERE resource_name = @ResourceName;
                """, new { ResourceName = resourceName });
            lock (_stateLock)
            {
                if (_resourceStates.TryGetValue(resourceName, out var state))
                {
                    state.Resource.ConsoleLogsLoaded = true;
                }
            }
        }
    }

    async Task IResourceRepositoryWriter.AddConsoleLogsAsync(string resourceName, IReadOnlyList<ConsoleLogLine> logLines)
    {
        EnsureWritable();
        ThrowIfDisposed();
        if (logLines.Count == 0)
        {
            return;
        }

        var viewModelLines = logLines.Select(line => new ResourceLogLine(line.LineNumber, line.Text, line.IsStdErr)).ToArray();
        Channel<IReadOnlyList<ResourceLogLine>>[] channels;
        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
        {
            int lastLineNumber;
            DateTime? clearDate;
            lock (_stateLock)
            {
                var state = _resourceStates.GetValueOrDefault(resourceName);
                lastLineNumber = state?.LastConsoleLogLineNumber ?? int.MinValue;
                clearDate = state?.ConsoleLogsClearDate;
            }

            // A response can overlap a previously persisted response or repeat a line number within the
            // same batch. Persist only new line numbers and keep the latest content for an in-batch repeat.
            var consoleLogsToInsert = new List<ConsoleLogToInsert>(logLines.Count);
            var consoleLogToInsertIndexes = new Dictionary<int, int>();
            foreach (var line in logLines)
            {
                if (line.LineNumber <= lastLineNumber)
                {
                    continue;
                }

                // AppHost subscriptions replay lines in this raw shape:
                //   2025-02-08T10:16:08Z Application started.
                // A resource can emit lines while the Console Logs page is closed, so its line number can
                // be new even though its timestamp predates a clear operation performed from Manage Data.
                if (clearDate is not null &&
                    TimestampParser.TryParseConsoleTimestamp(line.Text, out var timestamp) &&
                    timestamp.Value.Timestamp.UtcDateTime <= clearDate)
                {
                    continue;
                }

                if (consoleLogToInsertIndexes.TryGetValue(line.LineNumber, out var index))
                {
                    consoleLogsToInsert[index] = new(line.LineNumber, line.Text, line.IsStdErr);
                }
                else
                {
                    consoleLogToInsertIndexes.Add(line.LineNumber, consoleLogsToInsert.Count);
                    consoleLogsToInsert.Add(new(line.LineNumber, line.Text, line.IsStdErr));
                }
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                UPDATE dashboard_resources
                SET console_logs_loaded = 1
                WHERE resource_name = @ResourceName;
                """, new { ResourceName = resourceName }, transaction);
            InsertConsoleLogs(connection, transaction, resourceName, consoleLogsToInsert);
            transaction.Commit();

            lock (_stateLock)
            {
                if (_resourceStates.TryGetValue(resourceName, out var state))
                {
                    state.LastConsoleLogLineNumber = Math.Max(state.LastConsoleLogLineNumber, logLines.Max(line => line.LineNumber));
                    state.Resource.ConsoleLogsLoaded = true;
                }
                channels = (_consoleChannels.GetValueOrDefault(resourceName) ?? []).ToArray();
            }
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(viewModelLines);
        }
    }

    /// <inheritdoc/>
    public async Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate)
    {
        EnsureWritable();
        ThrowIfDisposed();
        if (resourceNames.Count == 0)
        {
            return;
        }

        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
        {
            // Keep the in-memory line-number watermark so an AppHost retry cannot repopulate rows
            // the user cleared. New lines with higher numbers continue to be persisted normally.
            using var connection = _database.OpenConnection();
            connection.Execute(
                "DELETE FROM console_logs WHERE resource_name IN @ResourceNames;",
                new { ResourceNames = resourceNames });

            lock (_stateLock)
            {
                foreach (var resourceName in resourceNames)
                {
                    if (_resourceStates.TryGetValue(resourceName, out var state))
                    {
                        state.ConsoleLogsClearDate = clearDate;
                    }
                }
            }
        }
    }

    private ResourceViewModel CreateViewModel(Resource resource, IReadOnlyDictionary<string, ResourceViewModel> resources)
    {
        if (resources.TryGetValue(resource.Name, out var existingResource))
        {
            var viewModel = resource.ToViewModel(existingResource.ReplicaIndex, _knownPropertyLookup, _logger);
            viewModel.ConsoleLogsLoaded = existingResource.ConsoleLogsLoaded;
            return viewModel;
        }

        var replicaIndex = resources.Values.Count(r => string.Equals(r.DisplayName, resource.DisplayName, StringComparisons.ResourceName)) + 1;
        return resource.ToViewModel(replicaIndex, _knownPropertyLookup, _logger);
    }

    private void LoadResources()
    {
        using var connection = _database.OpenConnection();
        foreach (var storedResource in LoadResourceRecords(connection))
        {
            var viewModel = storedResource.Resource.ToViewModel(storedResource.ReplicaIndex, _knownPropertyLookup, _logger);
            viewModel.ConsoleLogsLoaded = storedResource.ConsoleLogsLoaded;
            _resourceStates[storedResource.Resource.Name] = new ResourceState(viewModel);
        }

        // Console line numbers restart at 1 for a new AppHost generation. After a Dashboard restart,
        // the repository cannot distinguish that new data from a same-generation replay, so Resume
        // favors preserving new logs and may duplicate a replay from an AppHost that remained running.
        // Repeat watches within this Dashboard process are still suppressed by the in-memory high-water mark.
    }

    private void UpdateResourceStates(IReadOnlyDictionary<string, ResourceViewModel> resources)
    {
        var updatedStates = new Dictionary<string, ResourceState>(StringComparers.ResourceName);
        foreach (var (resourceName, resource) in resources)
        {
            if (_resourceStates.TryGetValue(resourceName, out var state))
            {
                state.Resource = resource;
            }
            else
            {
                state = new ResourceState(resource);
            }
            updatedStates.Add(resourceName, state);
        }

        _resourceStates.Clear();
        foreach (var (resourceName, state) in updatedStates)
        {
            _resourceStates.Add(resourceName, state);
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> ReadResourceUpdatesAsync(
        Channel<IReadOnlyList<ResourceViewModelChange>> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var changes in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return changes;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _resourceChannels = _resourceChannels.Remove(channel);
            }
        }
    }

    private void PublishResourceChanges(IReadOnlyList<ResourceViewModelChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Channel<IReadOnlyList<ResourceViewModelChange>>[] channels;
        lock (_stateLock)
        {
            channels = _resourceChannels.ToArray();
        }
        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(changes);
        }
    }

    private void EnsureWritable()
    {
        _database.EnsureWritable("Historical dashboard resources are read-only.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        using (_database.WriteLock.Lock())
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Channel<IReadOnlyList<ResourceViewModelChange>>[] resourceChannels;
            Channel<IReadOnlyList<ResourceLogLine>>[] consoleChannels;
            lock (_stateLock)
            {
                resourceChannels = _resourceChannels.ToArray();
                consoleChannels = _consoleChannels.Values.SelectMany(channels => channels).ToArray();
            }

            foreach (var channel in resourceChannels)
            {
                channel.Writer.TryComplete();
            }
            foreach (var channel in consoleChannels)
            {
                channel.Writer.TryComplete();
            }

        }
    }

    private sealed class ConsoleLogRecord
    {
        public required int LineNumber { get; init; }
        public required string Content { get; init; }
        public required bool IsStdErr { get; init; }
    }

    private sealed class ResourceState(ResourceViewModel resource)
    {
        public ResourceViewModel Resource { get; set; } = resource;
        public int LastConsoleLogLineNumber { get; set; } = int.MinValue;
        public DateTime? ConsoleLogsClearDate { get; set; }
    }
}