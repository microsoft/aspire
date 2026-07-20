// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates telemetry and resource repositories for dashboard run databases.
/// </summary>
// IServiceProvider is required to defer resolving IOutgoingPeerResolver until a telemetry repository is created.
// Constructor injection would create a cycle through ResourceOutgoingPeerResolver and IResourceRepository.
internal sealed class RepositoryFactory(IServiceProvider serviceProvider) : IRepositoryFactory
{
    public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) =>
        new SqliteTelemetryRepository(
            database,
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            serviceProvider.GetRequiredService<IOptions<DashboardOptions>>(),
            serviceProvider.GetRequiredService<PauseManager>(),
            serviceProvider.GetServices<IOutgoingPeerResolver>());

    public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) =>
        new SqliteResourceRepository(
            database,
            serviceProvider.GetRequiredService<IKnownPropertyLookup>(),
            serviceProvider.GetRequiredService<ILoggerFactory>());
}