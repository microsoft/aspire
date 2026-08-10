// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Dashboard.Tests.Shared;

internal static class TestDashboardDataSource
{
    public static DashboardDataSource Create(
        IDashboardRunStore runStore,
        DashboardDataSourcePool dataSourcePool)
    {
        return new DashboardDataSource(
            runStore,
            NullLogger<DashboardDataSource>.Instance,
            dataSourcePool);
    }

    public static DashboardDataSourcePool CreatePool(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository,
        IDashboardRunStore runStore)
    {
        return new DashboardDataSourcePool(
            runStore,
            new TestRepositoryFactory(telemetryRepository, resourceRepository));
    }

    private sealed class TestRepositoryFactory(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository) : IRepositoryFactory
    {
        public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) => telemetryRepository;
        public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) => resourceRepository;
    }
}

internal sealed class TestDashboardRunStore(
    IEnumerable<DashboardRunDescriptor>? runs = null,
    Func<DashboardRunDescriptor, IDisposable?>? tryAcquireRunLease = null,
    string? databasePath = null) : IDashboardRunStore
{
    private readonly IReadOnlyList<DashboardRunDescriptor> _runs = (runs ??
        [new("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", databasePath ?? string.Empty, IsCurrent: true)])
        .ToArray();

    public bool SupportsRunSelection => _runs.Any(run => !run.IsCurrent);

    public IReadOnlyList<DashboardRunDescriptor> GetRuns() => _runs;

    public DashboardRunDescriptor GetCurrentRun() => _runs.Single(run => run.IsCurrent);

    public DashboardRunDescriptor? GetRunById(string runId) =>
        _runs.SingleOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));

    public void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        _runs.Single(candidate => string.Equals(candidate.RunId, run.RunId, StringComparison.Ordinal)).IsPinned = isPinned;
    }

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => tryAcquireRunLease?.Invoke(run);

    public void PublishRun()
    {
    }

    public void PruneExpiredRuns()
    {
    }
}