// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;

namespace Aspire.Dashboard.Tests.Shared;

internal static class TestDashboardDataSource
{
    public static DashboardDataSource Create(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository)
    {
        return new DashboardDataSource(
            new TestRunStore(),
            telemetryRepository,
            resourceRepository,
            new TestRepositoryFactory(telemetryRepository, resourceRepository));
    }

    private sealed class TestRunStore : IDashboardRunStore
    {
        public bool SupportsRunSelection => false;

        public IReadOnlyList<DashboardRunDescriptor> GetRuns() =>
            [new("current", DateTimeOffset.UnixEpoch, null, false, "TestApp", string.Empty, IsCurrent: true)];
    }

    private sealed class TestRepositoryFactory(
        ITelemetryRepository telemetryRepository,
        IResourceRepository resourceRepository) : IRepositoryFactory
    {
        public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) => telemetryRepository;
        public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) => resourceRepository;
        public void Dispose()
        {
        }
    }
}