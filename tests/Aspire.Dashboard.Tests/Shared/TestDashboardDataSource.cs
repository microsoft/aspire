// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Dashboard.Tests.Shared;

internal static class TestDashboardDataSource
{
    public static DashboardDataSource Create(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository)
    {
        return new DashboardDataSource(
            new TestDashboardRunStore(),
            telemetryRepository,
            resourceRepository,
            new TestRepositoryFactory(telemetryRepository, resourceRepository),
            NullLogger<DashboardDataSource>.Instance);
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
    IReadOnlyList<DashboardRunDescriptor>? runs = null,
    Func<DashboardRunDescriptor, IDisposable?>? tryAcquireRunLease = null) : IDashboardRunStore
{
    private readonly IReadOnlyList<DashboardRunDescriptor> _runs = runs ??
        [new("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", string.Empty, IsCurrent: true)];

    public bool SupportsRunSelection => _runs.Any(run => !run.IsCurrent);

    public IReadOnlyList<DashboardRunDescriptor> GetRuns() => _runs;

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => tryAcquireRunLease?.Invoke(run);
}