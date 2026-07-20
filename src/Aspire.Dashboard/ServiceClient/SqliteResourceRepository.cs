// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
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
    private readonly bool _ownsDatabase;
    private readonly object _lock = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private ImmutableHashSet<Channel<IReadOnlyList<ResourceViewModelChange>>> _resourceChannels = [];
    private readonly Dictionary<string, ImmutableHashSet<Channel<IReadOnlyList<ResourceLogLine>>>> _consoleChannels = new(StringComparers.ResourceName);
    private readonly Dictionary<string, int> _lastConsoleLogLineNumbers = new(StringComparers.ResourceName);
    private bool _disposed;

    public SqliteResourceRepository(
        string databasePath,
        IKnownPropertyLookup knownPropertyLookup,
        ILoggerFactory loggerFactory,
        bool readOnly = false)
        : this(new DashboardSqliteDatabase(databasePath, readOnly), knownPropertyLookup, loggerFactory)
    {
        _ownsDatabase = true;
    }

    internal SqliteResourceRepository(
        DashboardSqliteDatabase database,
        IKnownPropertyLookup knownPropertyLookup,
        ILoggerFactory loggerFactory)
    {
        _database = database;
        _knownPropertyLookup = knownPropertyLookup;
        _logger = loggerFactory.CreateLogger<SqliteResourceRepository>();

        if (!database.IsReadOnly)
        {
            database.InitializeSchema();
        }

        LoadResources();
    }

    public ResourceViewModel? GetResource(string resourceName)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _resources.GetValueOrDefault(resourceName);
        }
    }

    public IReadOnlyList<ResourceViewModel> GetResources()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _resources.Values.ToList();
        }
    }

    public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
            _resourceChannels = _resourceChannels.Add(channel);

            return Task.FromResult(new ResourceViewModelSubscription(
                _resources.Values.ToImmutableArray(),
                ReadResourceUpdatesAsync(channel, cancellationToken)));
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(
        string resourceName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ResourceLogLine[] lines;
        lock (_lock)
        {
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
        }

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
        lock (_lock)
        {
            ThrowIfDisposed();
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
            _consoleChannels[resourceName] = (_consoleChannels.GetValueOrDefault(resourceName) ?? []).Add(channel);
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
            lock (_lock)
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

    void IResourceRepositoryWriter.ReplaceResources(IReadOnlyList<Resource> resources)
    {
        EnsureWritable();
        List<ResourceViewModelChange> changes = [];

        lock (_lock)
        {
            ThrowIfDisposed();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var replacementResourceNames = resources.Select(resource => resource.Name).ToHashSet(StringComparers.ResourceName);
            foreach (var (resourceName, viewModel) in _resources)
            {
                if (!replacementResourceNames.Contains(resourceName))
                {
                    changes.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Delete, viewModel));
                }
            }
            var resourcesWithLoadedConsoleLogs = _resources.Values
                .Where(resource => resource.ConsoleLogsLoaded)
                .Select(resource => resource.Name)
                .ToHashSet(StringComparers.ResourceName);

            connection.Execute("DELETE FROM dashboard_resources;", transaction: transaction);
            _resources.Clear();
            var resourcesToSave = new List<ResourceToSave>(resources.Count);

            foreach (var resource in resources)
            {
                var viewModel = CreateViewModel(resource);
                viewModel.ConsoleLogsLoaded = resourcesWithLoadedConsoleLogs.Contains(resource.Name);
                resourcesToSave.Add(new(resource, viewModel.ReplicaIndex, viewModel.ConsoleLogsLoaded));
                _resources[resource.Name] = viewModel;
                changes.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
            }

            InsertResources(connection, transaction, resourcesToSave);
            transaction.Commit();
        }

        PublishResourceChanges(changes);
    }

    void IResourceRepositoryWriter.ApplyChanges(IReadOnlyList<WatchResourcesChange> changes)
    {
        EnsureWritable();
        List<ResourceViewModelChange> viewModelChanges = [];

        lock (_lock)
        {
            ThrowIfDisposed();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
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

            foreach (var change in changes)
            {
                if (change.KindCase == WatchResourcesChange.KindOneofCase.Upsert)
                {
                    var resource = change.Upsert;
                    var viewModel = CreateViewModel(resource);
                    resourcesToSave[resource.Name] = new(resource, viewModel.ReplicaIndex, viewModel.ConsoleLogsLoaded);
                    _resources[resource.Name] = viewModel;
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
                }
                else if (change.KindCase == WatchResourcesChange.KindOneofCase.Delete && _resources.Remove(change.Delete.ResourceName, out var removed))
                {
                    resourcesToSave.Remove(change.Delete.ResourceName);
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Delete, removed));
                }
            }

            connection.Execute("DELETE FROM dashboard_resources WHERE resource_name IN @ResourceNames;", new { ResourceNames = affectedResourceNames }, transaction);
            InsertResources(connection, transaction, resourcesToSave.Values.ToArray());
            transaction.Commit();
        }

        PublishResourceChanges(viewModelChanges);
    }

    void IResourceRepositoryWriter.MarkConsoleLogsLoaded(string resourceName)
    {
        EnsureWritable();
        lock (_lock)
        {
            ThrowIfDisposed();
            using var connection = _database.OpenConnection();
            connection.Execute("""
                UPDATE dashboard_resources
                SET console_logs_loaded = 1
                WHERE resource_name = @ResourceName;
                """, new { ResourceName = resourceName });
            if (_resources.TryGetValue(resourceName, out var resource))
            {
                resource.ConsoleLogsLoaded = true;
            }
        }
    }

    void IResourceRepositoryWriter.AddConsoleLogs(string resourceName, IReadOnlyList<ConsoleLogLine> logLines)
    {
        EnsureWritable();
        if (logLines.Count == 0)
        {
            return;
        }

        var viewModelLines = logLines.Select(line => new ResourceLogLine(line.LineNumber, line.Text, line.IsStdErr)).ToArray();
        Channel<IReadOnlyList<ResourceLogLine>>[] channels;
        lock (_lock)
        {
            ThrowIfDisposed();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            connection.Execute("""
                UPDATE dashboard_resources
                SET console_logs_loaded = 1
                WHERE resource_name = @ResourceName;
                """, new { ResourceName = resourceName }, transaction);
            var lastLineNumber = _lastConsoleLogLineNumbers.GetValueOrDefault(resourceName, int.MinValue);
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
            InsertConsoleLogs(connection, transaction, resourceName, consoleLogsToInsert);
            transaction.Commit();
            if (consoleLogsToInsert.Count > 0)
            {
                _lastConsoleLogLineNumbers[resourceName] = consoleLogsToInsert.Max(line => line.LineNumber);
            }
            if (_resources.TryGetValue(resourceName, out var resource))
            {
                resource.ConsoleLogsLoaded = true;
            }
            channels = (_consoleChannels.GetValueOrDefault(resourceName) ?? []).ToArray();
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(viewModelLines);
        }
    }

    private ResourceViewModel CreateViewModel(Resource resource)
    {
        if (_resources.TryGetValue(resource.Name, out var existingResource))
        {
            var viewModel = resource.ToViewModel(existingResource.ReplicaIndex, _knownPropertyLookup, _logger);
            viewModel.ConsoleLogsLoaded = existingResource.ConsoleLogsLoaded;
            return viewModel;
        }

        var replicaIndex = _resources.Values.Count(r => string.Equals(r.DisplayName, resource.DisplayName, StringComparisons.ResourceName)) + 1;
        return resource.ToViewModel(replicaIndex, _knownPropertyLookup, _logger);
    }

    private void LoadResources()
    {
        using var connection = _database.OpenConnection();
        foreach (var storedResource in LoadResourceRecords(connection))
        {
            var viewModel = storedResource.Resource.ToViewModel(storedResource.ReplicaIndex, _knownPropertyLookup, _logger);
            viewModel.ConsoleLogsLoaded = storedResource.ConsoleLogsLoaded;
            _resources[storedResource.Resource.Name] = viewModel;
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
            lock (_lock)
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
        lock (_lock)
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            foreach (var channel in _resourceChannels)
            {
                channel.Writer.TryComplete();
            }
            foreach (var channels in _consoleChannels.Values)
            {
                foreach (var channel in channels)
                {
                    channel.Writer.TryComplete();
                }
            }

            _database.ClearPool();
            if (_ownsDatabase)
            {
                _database.Dispose();
            }
        }
    }

    private sealed class ConsoleLogRecord
    {
        public required int LineNumber { get; init; }
        public required string Content { get; init; }
        public required bool IsStdErr { get; init; }
    }
}