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
    private readonly object _lock = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private ImmutableHashSet<Channel<IReadOnlyList<ResourceViewModelChange>>> _resourceChannels = [];
    private readonly Dictionary<string, ImmutableHashSet<Channel<IReadOnlyList<ResourceLogLine>>>> _consoleChannels = new(StringComparers.ResourceName);
    private readonly Dictionary<string, Dictionary<int, long>> _consoleLogIds = new(StringComparers.ResourceName);
    private bool _disposed;

    public SqliteResourceRepository(
        string databasePath,
        IKnownPropertyLookup knownPropertyLookup,
        ILoggerFactory loggerFactory,
        bool readOnly = false)
        : this(new DashboardSqliteDatabase(databasePath, readOnly), knownPropertyLookup, loggerFactory)
    {
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
            connection.Execute("DELETE FROM dashboard_resources;", transaction: transaction);
            _resources.Clear();

            foreach (var resource in resources)
            {
                var viewModel = CreateViewModel(resource);
                SaveResource(connection, transaction, resource, viewModel.ReplicaIndex);
                _resources[resource.Name] = viewModel;
                changes.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
            }

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

            foreach (var change in changes)
            {
                if (change.KindCase == WatchResourcesChange.KindOneofCase.Upsert)
                {
                    var resource = change.Upsert;
                    var viewModel = CreateViewModel(resource);
                    SaveResource(connection, transaction, resource, viewModel.ReplicaIndex);
                    _resources[resource.Name] = viewModel;
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, viewModel));
                }
                else if (change.KindCase == WatchResourcesChange.KindOneofCase.Delete && _resources.Remove(change.Delete.ResourceName, out var removed))
                {
                    connection.Execute("DELETE FROM dashboard_resources WHERE resource_name = @ResourceName;", new { ResourceName = change.Delete.ResourceName }, transaction);
                    viewModelChanges.Add(new ResourceViewModelChange(ResourceViewModelChangeType.Delete, removed));
                }
            }

            transaction.Commit();
        }

        PublishResourceChanges(viewModelChanges);
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
            if (!_consoleLogIds.TryGetValue(resourceName, out var resourceLogIds))
            {
                resourceLogIds = [];
                _consoleLogIds.Add(resourceName, resourceLogIds);
            }

            foreach (var line in logLines)
            {
                var parameters = new
                {
                    ResourceName = resourceName,
                    line.LineNumber,
                    Content = line.Text,
                    line.IsStdErr
                };
                if (resourceLogIds.TryGetValue(line.LineNumber, out var consoleLogId))
                {
                    connection.Execute("""
                        UPDATE console_logs
                        SET content = @Content, is_stderr = @IsStdErr
                        WHERE console_log_id = @ConsoleLogId;
                        """, new { parameters.Content, parameters.IsStdErr, ConsoleLogId = consoleLogId }, transaction);
                }
                else
                {
                    consoleLogId = connection.QuerySingle<long>("""
                        INSERT INTO console_logs (resource_name, line_number, content, is_stderr)
                        VALUES (@ResourceName, @LineNumber, @Content, @IsStdErr)
                        RETURNING console_log_id;
                        """, parameters, transaction);
                    resourceLogIds.Add(line.LineNumber, consoleLogId);
                }
            }
            transaction.Commit();
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
            return resource.ToViewModel(existingResource.ReplicaIndex, _knownPropertyLookup, _logger);
        }

        var replicaIndex = _resources.Values.Count(r => string.Equals(r.DisplayName, resource.DisplayName, StringComparisons.ResourceName)) + 1;
        return resource.ToViewModel(replicaIndex, _knownPropertyLookup, _logger);
    }

    private void LoadResources()
    {
        using var connection = _database.OpenConnection();
        foreach (var storedResource in LoadResourceRecords(connection))
        {
            _resources[storedResource.Resource.Name] = storedResource.Resource.ToViewModel(storedResource.ReplicaIndex, _knownPropertyLookup, _logger);
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
        }
    }

    private sealed class ConsoleLogRecord
    {
        public required int LineNumber { get; init; }
        public required string Content { get; init; }
        public required bool IsStdErr { get; init; }
    }
}