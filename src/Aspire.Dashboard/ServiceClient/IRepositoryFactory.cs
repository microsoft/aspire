// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates repositories for current and historical dashboard run databases.
/// </summary>
internal interface IRepositoryFactory
{
    /// <summary>
    /// Creates a telemetry repository for the specified database.
    /// </summary>
    ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database);

    /// <summary>
    /// Creates a resource repository for the specified database.
    /// </summary>
    IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database);
}