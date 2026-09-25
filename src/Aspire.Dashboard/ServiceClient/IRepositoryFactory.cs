// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Creates repositories for current and historical dashboard run databases.
/// </summary>
public interface IRepositoryFactory
{
    /// <summary>
    /// Creates a telemetry repository for the specified database.
    /// </summary>
    /// <param name="database">The dashboard database used by the repository.</param>
    /// <returns>A telemetry repository for the specified database.</returns>
    ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database);

    /// <summary>
    /// Creates a resource repository for the specified database.
    /// </summary>
    /// <param name="database">The dashboard database used by the repository.</param>
    /// <returns>A resource repository for the specified database.</returns>
    IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database);
}