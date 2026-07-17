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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
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

        using var runStore = CreateRunStore(options);

        var applicationDirectoryName = DashboardRunStore.GetApplicationDirectoryName("My Dashboard");
        var expectedRunsDirectory = Path.Combine(_temporaryDirectory, applicationDirectoryName, "runs");
        Assert.Equal(expectedRunsDirectory, Directory.GetParent(runStore.RunDirectory)!.FullName);
    }

    [Fact]
    public void NoneMode_UsesTemporaryDatabaseAndDeletesItOnDispose()
    {
        var options = CreateOptions(persistenceMode: DashboardPersistenceMode.None);
        string runDirectory;
        string databasePath;

        using (var runStore = CreateRunStore(options))
        {
            runDirectory = runStore.RunDirectory;
            databasePath = runStore.DatabasePath;
            new DashboardSqliteDatabase(databasePath).InitializeSchema();

            Assert.False(runStore.SupportsRunSelection);
            Assert.False(runDirectory.StartsWith(_temporaryDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Collection(runStore.GetRuns(), run => Assert.True(run.IsCurrent));
            Assert.True(File.Exists(databasePath));
        }

        Assert.False(Directory.Exists(runDirectory));
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public void NoneMode_DeletesAbandonedTemporaryDirectories()
    {
        var abandonedDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-").FullName;
        File.WriteAllText(Path.Combine(abandonedDirectory, "dashboard.db"), string.Empty);

        try
        {
            using var runStore = CreateRunStore(CreateOptions(persistenceMode: DashboardPersistenceMode.None));

            Assert.False(Directory.Exists(abandonedDirectory));
        }
        finally
        {
            if (Directory.Exists(abandonedDirectory))
            {
                Directory.Delete(abandonedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NoneMode_DoesNotDeleteActiveTemporaryDirectories()
    {
        var options = CreateOptions(persistenceMode: DashboardPersistenceMode.None);
        using var activeRunStore = CreateRunStore(options);
        new DashboardSqliteDatabase(activeRunStore.DatabasePath).InitializeSchema();

        using var secondRunStore = CreateRunStore(options);

        Assert.True(Directory.Exists(activeRunStore.RunDirectory));
        Assert.True(Directory.Exists(secondRunStore.RunDirectory));
    }

    [Fact]
    public void NoneMode_DoesNotDeleteOtherDashboardTemporaryDirectories()
    {
        var otherDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-telemetry-tests-").FullName;
        File.WriteAllText(Path.Combine(otherDirectory, "dashboard.db"), string.Empty);

        try
        {
            using var runStore = CreateRunStore(CreateOptions(persistenceMode: DashboardPersistenceMode.None));

            Assert.True(Directory.Exists(otherDirectory));
        }
        finally
        {
            if (Directory.Exists(otherDirectory))
            {
                Directory.Delete(otherDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void AppendMode_ReusesApplicationDatabaseWithoutRunSelection()
    {
        var options = CreateOptions("My Dashboard", DashboardPersistenceMode.Append);
        string firstDatabasePath;

        using (var firstRunStore = CreateRunStore(options))
        {
            firstDatabasePath = firstRunStore.DatabasePath;
            new DashboardSqliteDatabase(firstDatabasePath).InitializeSchema();
        }

        using var secondRunStore = CreateRunStore(options);

        Assert.Equal(firstDatabasePath, secondRunStore.DatabasePath);
        Assert.False(secondRunStore.SupportsRunSelection);
        Assert.Collection(secondRunStore.GetRuns(), run => Assert.True(run.IsCurrent));
        Assert.True(DashboardSqliteDatabase.IsCompatible(secondRunStore.DatabasePath));
    }

    [Fact]
    public void AppendMode_DeletesIncompatibleDatabase()
    {
        var options = CreateOptions(persistenceMode: DashboardPersistenceMode.Append);
        string databasePath;

        using (var firstRunStore = CreateRunStore(options))
        {
            databasePath = firstRunStore.DatabasePath;
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (1);";
            command.ExecuteNonQuery();
        }

        using var secondRunStore = CreateRunStore(options);

        Assert.Equal(databasePath, secondRunStore.DatabasePath);
        Assert.False(File.Exists(databasePath));
        new DashboardSqliteDatabase(databasePath).InitializeSchema();
        Assert.True(DashboardSqliteDatabase.IsCompatible(databasePath));
    }

    [Theory]
    [InlineData("CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (8), (8);")]
    [InlineData("CREATE TABLE dashboard_schema (version); INSERT INTO dashboard_schema VALUES ('invalid');")]
    public void IsCompatible_ReturnsFalseForMalformedSchema(string schemaSql)
    {
        var databasePath = Path.Combine(_temporaryDirectory, $"malformed-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = schemaSql;
            command.ExecuteNonQuery();
        }

        Assert.False(DashboardSqliteDatabase.IsCompatible(databasePath));
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

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var telemetryRepository = CreateTelemetryRepository(historicalRunStore.DatabasePath, options);
        }

        using var currentRunStore = CreateRunStore(options);
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
    public void RunsMode_DeletesOldestRunWhenLimitIsExceeded()
    {
        var applicationDirectory = Path.Combine(_temporaryDirectory, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"))
            .ToList();

        foreach (var directory in historicalRunDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        using var currentRunStore = CreateRunStore(CreateOptions());

        Assert.Equal(DashboardRunStore.MaxRuns, Directory.GetDirectories(runsDirectory).Length);
        Assert.False(Directory.Exists(historicalRunDirectories[^1]));
        Assert.All(historicalRunDirectories[..^1], directory => Assert.True(Directory.Exists(directory)));
        Assert.True(Directory.Exists(currentRunStore.RunDirectory));
    }

    [Fact]
    public void RunsMode_DoesNotDeleteActiveExpiredRun()
    {
        var applicationDirectory = Path.Combine(_temporaryDirectory, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"))
            .ToList();

        foreach (var directory in historicalRunDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        var activeExpiredRun = historicalRunDirectories[^1];
        using var activeRunLock = new FileStream(
            DashboardRunStore.GetRunLockPath(activeExpiredRun),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        using var currentRunStore = CreateRunStore(CreateOptions());

        Assert.True(Directory.Exists(activeExpiredRun));
        Assert.Equal(DashboardRunStore.MaxRuns + 1, Directory.GetDirectories(runsDirectory).Length);
    }

    [Fact]
    public void RunsMode_DeleteExpiredRunFails_LogsWarningAndContinues()
    {
        var applicationDirectory = Path.Combine(_temporaryDirectory, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"))
            .ToList();

        foreach (var directory in historicalRunDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(testSink)));
        var logger = loggerFactory.CreateLogger<DashboardRunStore>();
        var expiredRunDirectory = historicalRunDirectories[^1];

        using var currentRunStore = new DashboardRunStore(
            CreateOptions(),
            logger,
            directory => throw new IOException($"The directory '{directory}' is in use."));

        var warning = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Warning, warning.LogLevel);
        Assert.Equal(typeof(DashboardRunStore).FullName, warning.LoggerName);
        Assert.Contains(expiredRunDirectory, warning.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(warning.Exception);
        Assert.True(Directory.Exists(expiredRunDirectory));
        Assert.True(Directory.Exists(currentRunStore.RunDirectory));
    }

    [Fact]
    public void GetRuns_ExcludesIncompatibleDatabaseWithoutDeletingIt()
    {
        var options = CreateOptions();
        string incompatibleDatabasePath;

        using (var incompatibleRunStore = CreateRunStore(options))
        {
            incompatibleDatabasePath = incompatibleRunStore.DatabasePath;
            using var connection = new SqliteConnection($"Data Source={incompatibleDatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (1);";
            command.ExecuteNonQuery();
        }

        using var currentRunStore = CreateRunStore(options);

        Assert.Collection(currentRunStore.GetRuns(), run => Assert.True(run.IsCurrent));
        Assert.True(File.Exists(incompatibleDatabasePath));
    }

    [Fact]
    public void SqliteDatabase_ConfiguresLikeAndForeignKeys()
    {
        var database = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory, "connection.db"));
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            Assert.True(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);

            command.CommandText = """
                SELECT
                    'Dashboard' = 'dashboard' COLLATE NOCASE,
                    'CAFE au lait' LIKE '%fe AU%',
                    'Delta' LIKE 'dE%',
                    (SELECT foreign_keys FROM pragma_foreign_keys());
                """;
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(1, reader.GetInt64(3));
        }

        database.ClearPool();
    }

    [Fact]
    public async Task SelectedHistoricalRun_ReplaysDataAndRejectsMutation()
    {
        var options = CreateOptions();
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
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

        using var currentRunStore = CreateRunStore(options);
        var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        using var repositoryFactory = CreateRepositoryFactory(currentDatabase, options);
        var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        var currentResourceRepository = repositoryFactory.CreateResourceRepository(currentDatabase);
        using var dataSource = new DashboardDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory);
        Assert.Empty(dataSource.TelemetryRepository.GetResources());

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
        Assert.Empty(currentTelemetryRepository.GetResources());
        Assert.Empty(currentResourceRepository.GetResources());

        await using var currentClient = new DashboardClient(
            NullLoggerFactory.Instance,
            new ConfigurationManager(),
            options,
            new MockKnownPropertyLookup(),
            new TestStringLocalizer<Resources.Resources>());
        IDashboardClient selectedClient = new SelectedDashboardClient(currentClient, dataSource);
        var connectionStateChangedCount = 0;
        selectedClient.ConnectionStateChanged += _ => connectionStateChangedCount++;

        currentClient.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);

        Assert.True(selectedClient.IsEnabled);
        Assert.True(selectedClient.WhenConnected.IsCompletedSuccessfully);
        Assert.Equal(DashboardConnectionState.Connected, selectedClient.ConnectionState);
        Assert.Equal(0, connectionStateChangedCount);
        await selectedClient.ReconnectAsync();

        dataSource.SelectRun(runId: null);

        Assert.Empty(dataSource.TelemetryRepository.GetResources());
        Assert.False(dataSource.IsReadOnly);

        Action<DashboardConnectionState> handler = _ => connectionStateChangedCount++;
        selectedClient.ConnectionStateChanged += handler;
        dataSource.SelectRun(historicalRunId);
        selectedClient.ConnectionStateChanged -= handler;

        currentClient.SetConnectionStateForTesting(DashboardConnectionState.Connected);
        Assert.Equal(0, connectionStateChangedCount);
    }

    [Fact]
    public void UnknownRunId_SelectsCurrentRun()
    {
        var options = CreateOptions();
        using var currentRunStore = CreateRunStore(options);
        var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        using var repositoryFactory = CreateRepositoryFactory(currentDatabase, options);
        var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        var currentResourceRepository = repositoryFactory.CreateResourceRepository(currentDatabase);
        using var dataSource = new DashboardDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory);
        dataSource.SelectRun("missing");

        Assert.False(dataSource.IsReadOnly);
        Assert.True(dataSource.SelectedRun.IsCurrent);
        Assert.Equal(currentRunStore.RunId, dataSource.SelectedRun.RunId);
        Assert.Same(currentResourceRepository, dataSource.ResourceRepository);
        Assert.Same(currentTelemetryRepository, dataSource.TelemetryRepository);
    }

    private IOptions<DashboardOptions> CreateOptions(
        string applicationName = "TestApp",
        DashboardPersistenceMode persistenceMode = DashboardPersistenceMode.Runs)
    {
        return Options.Create(new DashboardOptions
        {
            ApplicationName = applicationName,
            Data = new DashboardDataOptions
            {
                Directory = _temporaryDirectory,
                PersistenceMode = persistenceMode
            }
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

    private static DashboardRunStore CreateRunStore(IOptions<DashboardOptions> options)
    {
        return new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance);
    }

    private static RepositoryFactory CreateRepositoryFactory(
        DashboardSqliteDatabase currentDatabase,
        IOptions<DashboardOptions> options)
    {
        return new RepositoryFactory(
            currentDatabase,
            NullLoggerFactory.Instance,
            options,
            new PauseManager(),
            static () => [],
            new MockKnownPropertyLookup());
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}