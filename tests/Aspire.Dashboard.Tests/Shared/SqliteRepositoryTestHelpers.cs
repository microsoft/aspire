// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Tests.Shared;

internal static class SqliteRepositoryTestHelpers
{
    public static async Task<SqliteRepositoryTestContext<SqliteTelemetryRepository>> CreateTelemetryRepositoryAsync(
        string databasePath,
        bool readOnly = false,
        bool pooling = false,
        ILoggerFactory? loggerFactory = null,
        IOptions<DashboardOptions>? dashboardOptions = null,
        PauseManager? pauseManager = null,
        TimeProvider? timeProvider = null,
        IEnumerable<IOutgoingPeerResolver>? outgoingPeerResolvers = null)
    {
        var database = new DashboardSqliteDatabase(databasePath, readOnly, pooling);
        try
        {
            if (!readOnly)
            {
                await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);
            }

            var repository = new SqliteTelemetryRepository(
                database,
                loggerFactory ?? NullLoggerFactory.Instance,
                dashboardOptions ?? Options.Create(new DashboardOptions()),
                pauseManager ?? new PauseManager(),
                timeProvider ?? TimeProvider.System,
                outgoingPeerResolvers ?? []);
            return new SqliteRepositoryTestContext<SqliteTelemetryRepository>(database, repository);
        }
        catch
        {
            database.ClearPool();
            database.Dispose();
            throw;
        }
    }

    public static SqliteRepositoryTestContext<SqliteResourceRepository> CreateResourceRepository(
        string databasePath,
        IKnownPropertyLookup knownPropertyLookup,
        bool readOnly = false,
        bool pooling = false,
        ILoggerFactory? loggerFactory = null)
    {
        var database = new DashboardSqliteDatabase(databasePath, readOnly, pooling);
        try
        {
            if (!readOnly)
            {
                database.InitializeSchemaAsync(cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }

            var repository = new SqliteResourceRepository(
                database,
                knownPropertyLookup,
                loggerFactory ?? NullLoggerFactory.Instance);
            return new SqliteRepositoryTestContext<SqliteResourceRepository>(database, repository);
        }
        catch
        {
            database.ClearPool();
            database.Dispose();
            throw;
        }
    }
}

internal sealed class SqliteRepositoryTestContext<TRepository>(
    DashboardSqliteDatabase database,
    TRepository repository) : IDisposable
    where TRepository : IDisposable
{
    public DashboardSqliteDatabase Database { get; } = database;

    public TRepository Repository { get; } = repository;

    public void Dispose()
    {
        try
        {
            Repository.Dispose();
        }
        finally
        {
            Database.ClearPool();
            Database.Dispose();
        }
    }
}
