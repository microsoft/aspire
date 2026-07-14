// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardDataSourceTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-runs-tests-").FullName;

    [Fact]
    public void RunDirectory_IsNestedUnderApplicationDirectoryAndRuns()
    {
        var options = CreateOptions("My Dashboard");

        using var runStore = new DashboardRunStore(options);

        var applicationDirectoryName = DashboardRunStore.GetApplicationDirectoryName("My Dashboard");
        var expectedRunsDirectory = Path.Combine(_temporaryDirectory, applicationDirectoryName, "runs");
        Assert.Equal(expectedRunsDirectory, Directory.GetParent(runStore.RunDirectory)!.FullName);
    }

    [Fact]
    public void ApplicationDirectoryName_IsSafeBoundedAndUnique()
    {
        var firstName = new string('a', 300) + "/dashboard";
        var secondName = new string('a', 300) + ":dashboard";

        var firstDirectoryName = DashboardRunStore.GetApplicationDirectoryName(firstName);
        var secondDirectoryName = DashboardRunStore.GetApplicationDirectoryName(secondName);

        Assert.Equal(DashboardRunStore.MaxApplicationDirectoryNameLength, firstDirectoryName.Length);
        Assert.Equal(DashboardRunStore.MaxApplicationDirectoryNameLength, secondDirectoryName.Length);
        Assert.Matches("^[A-Za-z0-9_-]+-[0-9a-f]{16}$", firstDirectoryName);
        Assert.Matches("^[A-Za-z0-9_-]+-[0-9a-f]{16}$", secondDirectoryName);
        Assert.NotEqual(firstDirectoryName, secondDirectoryName);
        Assert.Equal(firstDirectoryName, DashboardRunStore.GetApplicationDirectoryName(firstName));
    }

    [Fact]
    public void GetRuns_ReturnsCurrentThenCompletedHistoricalRun()
    {
        var options = CreateOptions();
        string historicalRunId;

        using (var historicalRunStore = new DashboardRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var telemetryRepository = CreateTelemetryRepository(historicalRunStore.DatabasePath, options);
        }

        using var currentRunStore = new DashboardRunStore(options);
        using var currentTelemetryRepository = CreateTelemetryRepository(currentRunStore.DatabasePath, options);

        Assert.Collection(
            currentRunStore.GetRuns(),
            currentRun =>
            {
                Assert.True(currentRun.IsCurrent);
                Assert.Equal(currentRunStore.RunId, currentRun.RunId);
            },
            historicalRun =>
            {
                Assert.False(historicalRun.IsCurrent);
                Assert.True(historicalRun.CleanShutdown);
                Assert.NotNull(historicalRun.EndedAtUtc);
                Assert.Equal(historicalRunId, historicalRun.RunId);
            });
    }

    [Fact]
    public void GetRuns_ExcludesIncompatibleDatabaseWithoutDeletingIt()
    {
        var options = CreateOptions();
        string incompatibleDatabasePath;

        using (var incompatibleRunStore = new DashboardRunStore(options))
        {
            incompatibleDatabasePath = incompatibleRunStore.DatabasePath;
            using var connection = new SqliteConnection($"Data Source={incompatibleDatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (1);";
            command.ExecuteNonQuery();
        }

        using var currentRunStore = new DashboardRunStore(options);

        Assert.Collection(currentRunStore.GetRuns(), run => Assert.True(run.IsCurrent));
        Assert.True(File.Exists(incompatibleDatabasePath));
    }

    [Fact]
    public void SqliteDatabase_ConfiguresExactStringFunctionsAndForeignKeys()
    {
        var database = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory, "connection.db"));
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                'Ångström' = 'ångström' COLLATE ORDINAL_IGNORE_CASE,
                ordinal_contains('CAFÉ au lait', 'fé AU'),
                ordinal_starts_with('Δelta', 'δE'),
                (SELECT foreign_keys FROM pragma_foreign_keys());
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public void SelectedHistoricalRun_ReplaysDataAndRejectsMutation()
    {
        var options = CreateOptions();
        string historicalRunId;

        using (var historicalRunStore = new DashboardRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var telemetryRepository = CreateTelemetryRepository(historicalRunStore.DatabasePath, options);
            telemetryRepository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = CreateScope("HistoricalLogger"),
                            LogRecords = { CreateLogRecord() }
                        }
                    }
                }
            });
            using var resourceRepository = CreateResourceRepository(historicalRunStore.DatabasePath);
            ((IResourceRepositoryWriter)resourceRepository).ReplaceResources([new Resource
            {
                Name = "api",
                DisplayName = "API",
                ResourceType = "Project",
                CreatedAt = Timestamp.FromDateTime(DateTime.UnixEpoch)
            }]);
        }

        using var currentRunStore = new DashboardRunStore(options);
        using var currentTelemetryRepository = CreateTelemetryRepository(currentRunStore.DatabasePath, options);
        using var currentResourceRepository = CreateResourceRepository(currentRunStore.DatabasePath);
        using var dataSource = CreateDataSource(
            currentRunStore,
            currentTelemetryRepository,
            currentResourceRepository,
            options);
        dataSource.SelectRun(historicalRunId);

        Assert.True(dataSource.IsReadOnly);
        Assert.Equal(historicalRunId, dataSource.SelectedRun.RunId);
        Assert.Equal("api", Assert.Single(dataSource.ResourceRepository.GetResources()).Name);
        Assert.Equal("TestService", Assert.Single(dataSource.TelemetryRepository.GetResources()).ResourceName);
        Assert.Equal("Test Value!", Assert.Single(dataSource.TelemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }).Items).Message);
        Assert.Throws<InvalidOperationException>(() => dataSource.TelemetryRepository.ClearMetrics());
    }

    [Fact]
    public void UnknownRunId_SelectsCurrentRun()
    {
        var options = CreateOptions();
        using var currentRunStore = new DashboardRunStore(options);
        using var currentTelemetryRepository = CreateTelemetryRepository(currentRunStore.DatabasePath, options);
        using var currentResourceRepository = CreateResourceRepository(currentRunStore.DatabasePath);
        using var dataSource = CreateDataSource(
            currentRunStore,
            currentTelemetryRepository,
            currentResourceRepository,
            options);
        dataSource.SelectRun("missing");

        Assert.False(dataSource.IsReadOnly);
        Assert.True(dataSource.SelectedRun.IsCurrent);
        Assert.Equal(currentRunStore.RunId, dataSource.SelectedRun.RunId);
        Assert.Same(currentResourceRepository, dataSource.ResourceRepository);
        Assert.Same(currentTelemetryRepository, dataSource.TelemetryRepository);
    }

    private IOptions<DashboardOptions> CreateOptions(string applicationName = "TestApp")
    {
        return Options.Create(new DashboardOptions
        {
            ApplicationName = applicationName,
            Data = new DashboardDataOptions { Directory = _temporaryDirectory }
        });
    }

    private static SqliteTelemetryRepository CreateTelemetryRepository(string databasePath, IOptions<DashboardOptions> options)
    {
        return new SqliteTelemetryRepository(
            databasePath,
            NullLoggerFactory.Instance,
            options,
            new PauseManager(),
            []);
    }

    private static SqliteResourceRepository CreateResourceRepository(string databasePath)
    {
        return new SqliteResourceRepository(databasePath, new MockKnownPropertyLookup(), NullLoggerFactory.Instance);
    }

    private static DashboardDataSource CreateDataSource(
        DashboardRunStore runStore,
        SqliteTelemetryRepository telemetryRepository,
        SqliteResourceRepository resourceRepository,
        IOptions<DashboardOptions> options)
    {
        return new DashboardDataSource(
            runStore,
            telemetryRepository,
            resourceRepository,
            NullLoggerFactory.Instance,
            options,
            new PauseManager(),
            [],
            new MockKnownPropertyLookup());
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}