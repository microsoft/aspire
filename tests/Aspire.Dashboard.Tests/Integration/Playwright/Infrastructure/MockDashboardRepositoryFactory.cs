// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal sealed class MockDashboardRepositoryFactory(IServiceProvider services, MockDashboardClient client) : IRepositoryFactory
{
    private readonly RepositoryFactory _repositoryFactory = new(services);

    public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) =>
        _repositoryFactory.CreateTelemetryRepository(database);

    public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) => client;
}
