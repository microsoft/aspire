// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Manages dashboard data sources and shares historical databases across dashboard circuits.
/// </summary>
public sealed class DashboardDataSourcePool : IDisposable
{
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(s_pathComparer);
    private readonly IDashboardRunStore _runStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private Lease? _current;
    private bool _pendingRunPublication;
    internal AsyncLock RunWriteLock { get; } = new();
    internal event Action? CurrentRunChanged;
    internal Task PruningTask { get; private set; } = Task.CompletedTask;

    internal async Task StartAppHostRunAsync(string appHostRunId, CancellationToken cancellationToken)
    {
        using (await RunWriteLock.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            Lease? retiredRun;
            lock (_lock)
            {
                if (_runStore is not DashboardRunStore store)
                {
                    return;
                }
                var runChanged = store.StartAppHostRun(appHostRunId, out var retiredRunOwnership);
                if (!runChanged && !_pendingRunPublication)
                {
                    return;
                }

                retiredRun = null;
                if (runChanged)
                {
                    // The store remembers the new AppHost ID before database initialization and metadata
                    // publication finish. Retry those steps for the same ID after a transient failure.
                    _pendingRunPublication = true;
                    retiredRun = _current;
                    if (retiredRun is not null)
                    {
                        // Retain the directory while old circuits and in-flight operations finish reading it.
                        retiredRun.RetainDirectory(retiredRunOwnership);
                        retiredRunOwnership = null;
                        _current = null;
                    }
                }
                retiredRunOwnership?.Dispose();
            }
            try
            {
                if (retiredRun is not null)
                {
                    await retiredRun.FreezeLiveUpdatesAsync().ConfigureAwait(false);
                }
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
                PublishRun();
                _pendingRunPublication = false;
            }
            finally
            {
                retiredRun?.Dispose();
            }
        }
        lock (_lock)
        {
            // Track all queued housekeeping, including an older task that has not started yet.
            PruningTask = Task.WhenAll(PruningTask, ((DashboardRunStore)_runStore).PruneExpiredRunsInBackground());
        }
        CurrentRunChanged?.Invoke();
    }

    internal bool IsCurrentDatabase(string databasePath)
    {
        lock (_lock)
        {
            return !_disposed && s_pathComparer.Equals(_current?.Database.DatabasePath, databasePath);
        }
    }

    internal IDisposable RetainCurrent(out Lease run, out DashboardRunDescriptor descriptor)
    {
        // Do not expose the replacement run before its schema is initialized.
        using var transition = RunWriteLock.Lock();
        lock (_lock)
        {
            run = Current;
            descriptor = _runStore.GetCurrentRun();
            return run.Retain();
        }
    }

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardDataSourcePool"/> class.
    /// </summary>
    /// <param name="runStore">The dashboard run store used to locate and lease dashboard runs.</param>
    /// <param name="repositoryFactory">The factory used to create repositories for dashboard runs.</param>
    public DashboardDataSourcePool(IDashboardRunStore runStore, IRepositoryFactory repositoryFactory)
    {
        _runStore = runStore;
        _repositoryFactory = repositoryFactory;
    }

    /// <summary>
    /// Gets the pool-owned data source lease for the current dashboard run.
    /// </summary>
    /// <remarks>
    /// Callers must not dispose the returned lease. It is shared by all callers and disposed with the pool.
    /// </remarks>
    public Lease Current
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var currentRun = _runStore.GetCurrentRun();
                if (_current is null)
                {
                    var database = new DashboardSqliteDatabase(currentRun.DatabasePath);
                    // A streaming request can release its scope after the host's root services are disposed.
                    // Keep temporary run files and their lock until that request closes its database lease.
                    var runStoreRetention = (_runStore as DashboardRunStore)?.Retain();
                    _current = new Lease(
                        database,
                        enableLivePeerResolution => _repositoryFactory.CreateTelemetryRepository(database, enableLivePeerResolution),
                        () => _repositoryFactory.CreateResourceRepository(database),
                        () =>
                        {
                            try
                            {
                                database.ClearPool();
                                database.Dispose();
                            }
                            finally
                            {
                                runStoreRetention?.Dispose();
                            }
                        });
                }

                return _current;
            }
        }
    }

    /// <summary>
    /// Attempts to acquire a shared data source lease for the specified dashboard run.
    /// </summary>
    /// <param name="run">The dashboard run to acquire.</param>
    /// <returns>A shared data source lease, or <see langword="null"/> when the run is no longer available.</returns>
    public Lease? TryAcquire(DashboardRunDescriptor run)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var databasePath = Path.GetFullPath(run.DatabasePath);
            if (!_entries.TryGetValue(databasePath, out var entry))
            {
                var runLease = _runStore.TryAcquireRunLease(run);
                if (runLease is null)
                {
                    return null;
                }

                DashboardSqliteDatabase? database = null;
                try
                {
                    database = new DashboardSqliteDatabase(databasePath, readOnly: true);
                    entry = new Entry(database, runLease);
                    _entries.Add(databasePath, entry);
                }
                catch
                {
                    database?.ClearPool();
                    database?.Dispose();
                    runLease.Dispose();
                    throw;
                }
            }

            entry.ReferenceCount++;
            try
            {
                if (!entry.Database.ValidateSchemaVersion(run.SchemaVersion))
                {
                    throw new InvalidOperationException(
                        $"Dashboard database for run '{run.RunId}' does not match run metadata schema version '{run.SchemaVersion}'.");
                }
            }
            catch
            {
                Release(entry);
                throw;
            }

            var lease = new Lease(
                entry.Database,
                _ => _repositoryFactory.CreateTelemetryRepository(entry.Database, enableLivePeerResolution: false),
                () => _repositoryFactory.CreateResourceRepository(entry.Database),
                () => Release(entry));
            try
            {
                _ = lease.TelemetryRepository;
                _ = lease.ResourceRepository;
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Current.Database.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void PublishRun()
    {
        _runStore.PublishRun();
    }

    internal void PruneExpiredRuns()
    {
        _runStore.PruneExpiredRuns();
    }

    private void Release(Entry entry)
    {
        lock (_lock)
        {
            if (entry.IsDisposed)
            {
                return;
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                _entries.Remove(entry.Database.DatabasePath);
                DisposeEntry(entry);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                DisposeEntry(entry);
            }
            _entries.Clear();
            _current?.Dispose();

        }
    }

    private static void DisposeEntry(Entry entry)
    {
        entry.IsDisposed = true;
        try
        {
            entry.Database.ClearPool();
            entry.Database.Dispose();
        }
        finally
        {
            entry.RunLease.Dispose();
        }
    }

    private sealed class Entry(DashboardSqliteDatabase database, IDisposable runLease)
    {
        public DashboardSqliteDatabase Database { get; } = database;
        public IDisposable RunLease { get; } = runLease;
        public int ReferenceCount { get; set; }
        public bool IsDisposed { get; set; }
    }

    /// <summary>
    /// Keeps repositories and their shared dashboard database available until the lease is disposed.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly Lazy<ITelemetryRepository> _telemetryRepository;
        private readonly object _telemetryLifecycleLock = new();
        private ITelemetryRepository? _telemetryRepositoryInstance;
        private bool _isRetired;
        private readonly Lazy<IResourceRepository> _resourceRepository;
        private readonly Action _release;
        private int _disposed;
        private int _references = 1;
        private IDisposable? _directoryLease;

        internal void RetainDirectory(IDisposable? directoryLease) => _directoryLease = directoryLease;

        internal async Task FreezeLiveUpdatesAsync()
        {
            SqliteTelemetryRepository? telemetryRepository;
            lock (_telemetryLifecycleLock)
            {
                // Retiring and first access can race. Construct late readers without live
                // subscriptions rather than materializing new services during rollover.
                _isRetired = true;
                telemetryRepository = _telemetryRepositoryInstance as SqliteTelemetryRepository;
            }
            if (telemetryRepository is not null)
            {
                await telemetryRepository.FreezePeerResolutionAsync().ConfigureAwait(false);
            }
        }

        internal IDisposable Retain()
        {
            Interlocked.Increment(ref _references);
            return new Reference(this);
        }

        private sealed class Reference(Lease owner) : IDisposable
        {
            private Lease? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }

        internal Lease(
            DashboardSqliteDatabase database,
            Func<bool, ITelemetryRepository> telemetryRepositoryFactory,
            Func<IResourceRepository> resourceRepositoryFactory,
            Action release)
        {
            Database = database;
            _telemetryRepository = new(() =>
            {
                lock (_telemetryLifecycleLock)
                {
                    _telemetryRepositoryInstance = telemetryRepositoryFactory(!_isRetired);
                    return _telemetryRepositoryInstance;
                }
            });
            _resourceRepository = new(resourceRepositoryFactory);
            _release = release;
        }

        /// <summary>
        /// Gets the shared dashboard database.
        /// </summary>
        public DashboardSqliteDatabase Database { get; }

        /// <summary>
        /// Gets the telemetry repository for the leased dashboard run.
        /// </summary>
        public ITelemetryRepository TelemetryRepository => _telemetryRepository.Value;

        /// <summary>
        /// Gets the resource repository for the leased dashboard run.
        /// </summary>
        public IResourceRepository ResourceRepository => _resourceRepository.Value;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                try
                {
                    if (_telemetryRepository.IsValueCreated)
                    {
                        _telemetryRepository.Value.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        if (_resourceRepository.IsValueCreated)
                        {
                            (_resourceRepository.Value as IDisposable)?.Dispose();
                        }
                    }
                    finally
                    {
                        try
                        {
                            _release();
                        }
                        finally
                        {
                            _directoryLease?.Dispose();
                        }
                    }
                }
            }
        }
    }
}