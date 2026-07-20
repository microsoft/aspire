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
internal sealed class RepositoryFactory(
    ILoggerFactory loggerFactory,
    IOptions<DashboardOptions> dashboardOptions,
    PauseManager pauseManager,
    Func<IEnumerable<IOutgoingPeerResolver>> outgoingPeerResolversAccessor,
    IKnownPropertyLookup knownPropertyLookup) : IRepositoryFactory
{
    public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) =>
        new SqliteTelemetryRepository(database, loggerFactory, dashboardOptions, pauseManager, outgoingPeerResolversAccessor());

    public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) =>
        new SqliteResourceRepository(database, knownPropertyLookup, loggerFactory);
}