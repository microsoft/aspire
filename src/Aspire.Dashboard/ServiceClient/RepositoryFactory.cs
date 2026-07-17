// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Shares repositories for the current database and creates dedicated repositories for historical databases.
/// </summary>
internal sealed class RepositoryFactory(
    DashboardSqliteDatabase currentDatabase,
    ILoggerFactory loggerFactory,
    IOptions<DashboardOptions> dashboardOptions,
    PauseManager pauseManager,
    Func<IEnumerable<IOutgoingPeerResolver>> outgoingPeerResolversAccessor,
    IKnownPropertyLookup knownPropertyLookup) : IRepositoryFactory
{
    private readonly object _telemetryLock = new();
    private readonly object _resourceLock = new();
    private ITelemetryRepository? _currentTelemetryRepository;
    private IResourceRepository? _currentResourceRepository;
    private int _disposed;

    public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ReferenceEquals(database, currentDatabase))
        {
            return CreateTelemetryRepositoryCore(database);
        }

        lock (_telemetryLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _currentTelemetryRepository ??= CreateTelemetryRepositoryCore(database);
        }
    }

    public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ReferenceEquals(database, currentDatabase))
        {
            return CreateResourceRepositoryCore(database);
        }

        lock (_resourceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _currentResourceRepository ??= CreateResourceRepositoryCore(database);
        }
    }

    private SqliteTelemetryRepository CreateTelemetryRepositoryCore(DashboardSqliteDatabase database) =>
        new(database, loggerFactory, dashboardOptions, pauseManager, outgoingPeerResolversAccessor());

    private SqliteResourceRepository CreateResourceRepositoryCore(DashboardSqliteDatabase database) =>
        new(database, knownPropertyLookup, loggerFactory);

    public void Dispose()
    {
        lock (_telemetryLock)
        {
            lock (_resourceLock)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _currentTelemetryRepository?.Dispose();
                (_currentResourceRepository as IDisposable)?.Dispose();
            }
        }
    }
}