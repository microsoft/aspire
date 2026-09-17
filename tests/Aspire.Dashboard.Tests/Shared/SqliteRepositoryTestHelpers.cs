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
    public static SqliteRepositoryTestContext<SqliteTelemetryRepository> CreateTemporaryTelemetryRepository(
        int? maxMetricsCount = null,
        int? maxAttributeCount = null,
        int? maxAttributeLength = null,
        int? maxSpanEventCount = null,
        int? maxTraceCount = null,
        int? maxLogCount = null,
        int? maxResourceCount = null,
        TimeSpan? subscriptionMinExecuteInterval = null,
        ILoggerFactory? loggerFactory = null,
        PauseManager? pauseManager = null,
        TimeProvider? timeProvider = null,
        IOutgoingPeerResolver[]? outgoingPeerResolvers = null)
    {
        var telemetryLimits = new TelemetryLimitOptions();
        telemetryLimits.MaxMetricsCount = maxMetricsCount ?? telemetryLimits.MaxMetricsCount;
        telemetryLimits.MaxAttributeCount = maxAttributeCount ?? telemetryLimits.MaxAttributeCount;
        telemetryLimits.MaxAttributeLength = maxAttributeLength ?? telemetryLimits.MaxAttributeLength;
        telemetryLimits.MaxSpanEventCount = maxSpanEventCount ?? telemetryLimits.MaxSpanEventCount;
        telemetryLimits.MaxTraceCount = maxTraceCount ?? telemetryLimits.MaxTraceCount;
        telemetryLimits.MaxLogCount = maxLogCount ?? telemetryLimits.MaxLogCount;
        telemetryLimits.MaxResourceCount = maxResourceCount ?? telemetryLimits.MaxResourceCount;

        var temporaryDirectory = Directory.CreateTempSubdirectory("aspire-tests-dashboard-telemetry-");
        try
        {
            var context = CreateTelemetryRepository(
                Path.Combine(temporaryDirectory.FullName, "dashboard.db"),
                pooling: true,
                loggerFactory: loggerFactory,
                dashboardOptions: Options.Create(new DashboardOptions { TelemetryLimits = telemetryLimits }),
                pauseManager: pauseManager,
                timeProvider: timeProvider,
                outgoingPeerResolvers: outgoingPeerResolvers);
            context.TemporaryDirectory = temporaryDirectory;

            if (subscriptionMinExecuteInterval is not null)
            {
                context.Repository.SubscriptionMinExecuteInterval = subscriptionMinExecuteInterval.Value;
            }

            return context;
        }
        catch
        {
            temporaryDirectory.Delete(recursive: true);
            throw;
        }
    }

    public static SqliteRepositoryTestContext<SqliteTelemetryRepository> CreateTelemetryRepository(
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
                database.InitializeSchemaAsync(cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }

            return CreateTelemetryRepository(
                database,
                loggerFactory,
                dashboardOptions,
                pauseManager,
                timeProvider,
                outgoingPeerResolvers);
        }
        catch
        {
            database.ClearPool();
            database.Dispose();
            throw;
        }
    }

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

            return CreateTelemetryRepository(
                database,
                loggerFactory,
                dashboardOptions,
                pauseManager,
                timeProvider,
                outgoingPeerResolvers);
        }
        catch
        {
            database.ClearPool();
            database.Dispose();
            throw;
        }
    }

    private static SqliteRepositoryTestContext<SqliteTelemetryRepository> CreateTelemetryRepository(
        DashboardSqliteDatabase database,
        ILoggerFactory? loggerFactory,
        IOptions<DashboardOptions>? dashboardOptions,
        PauseManager? pauseManager,
        TimeProvider? timeProvider,
        IEnumerable<IOutgoingPeerResolver>? outgoingPeerResolvers)
    {
        var repository = new SqliteTelemetryRepository(
            database,
            loggerFactory ?? NullLoggerFactory.Instance,
            dashboardOptions ?? Options.Create(new DashboardOptions()),
            pauseManager ?? new PauseManager(),
            timeProvider ?? TimeProvider.System,
            outgoingPeerResolvers ?? []);
        return new SqliteRepositoryTestContext<SqliteTelemetryRepository>(database, repository);
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
    internal DirectoryInfo? TemporaryDirectory { get; set; }

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
            TemporaryDirectory?.Delete(recursive: true);
        }
    }
}
