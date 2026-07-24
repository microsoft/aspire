// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.DashboardService.Proto.V1;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Shared;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardDataSourceTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);
    private readonly List<ServiceProvider> _serviceProviders = [];

    [Fact]
    public void RunDirectory_IsNestedUnderApplicationDirectoryAndRuns()
    {
        var options = CreateOptions("My Dashboard");

        using var runStore = CreateRunStore(options);

        var applicationDirectoryName = DashboardRunStore.GetApplicationDirectoryName("My Dashboard");
        var expectedRunsDirectory = Path.Combine(_workspace.Path, applicationDirectoryName, "runs");
        Assert.Equal(expectedRunsDirectory, Directory.GetParent(runStore.RunDirectory)!.FullName);
    }

    [Fact]
    public void ApplicationDirectory_WithoutDataDirectory_UsesDashboardDirectoryInAspireHome()
    {
        var applicationDirectoryName = DashboardRunStore.GetApplicationDirectoryName("My Dashboard");
        var expectedDirectory = Path.Combine(
            AspireHomeDirectory.GetDefault(),
            "dashboard",
            applicationDirectoryName);

        Assert.Equal(expectedDirectory, DashboardRunStore.GetApplicationDirectory(dataRoot: null, "My Dashboard"));
    }

    [Fact]
    public void RunId_IsUtcTimestampWithMillisecondPrecision()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 34, 56, 789, TimeSpan.Zero));

        using var runStore = CreateRunStore(CreateOptions(), timeProvider);

        Assert.Equal("20260720T123456789Z", runStore.RunId);
        Assert.Equal(runStore.RunId, Path.GetFileName(runStore.RunDirectory));
    }

    [Fact]
    public void RunMetadata_IncludesSchemaVersion()
    {
        using var runStore = CreateRunStore(CreateOptions());
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(runStore.RunDirectory, "run.json")));

        Assert.Equal(DashboardRunStore.SchemaVersion, metadata.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(DashboardRunStore.SchemaVersion, Assert.Single(runStore.GetRuns()).SchemaVersion);
    }

    [Fact]
    public void ConstructionAndGetRuns_LogResolvedStorageAndDiscoveredRuns()
    {
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        using var runStore = new DashboardRunStore(
            CreateOptions(),
            loggerFactory.CreateLogger<DashboardRunStore>(),
            TimeProvider.System);

        Assert.Single(runStore.GetRuns());

        Assert.Collection(
            testSink.Writes,
            initializationLog =>
            {
                Assert.Equal(LogLevel.Debug, initializationLog.LogLevel);
                Assert.Equal(
                    $"Dashboard run store initialized with persistence mode 'Run'. Run directory: '{runStore.RunDirectory}'. Database path: '{runStore.DatabasePath}'.",
                    initializationLog.Message);
            },
            discoveryLog =>
            {
                Assert.Equal(LogLevel.Debug, discoveryLog.LogLevel);
                Assert.Equal(
                    $"Dashboard run discovery completed in directory '{Directory.GetParent(runStore.RunDirectory)!.FullName}'. Run count: 1. Run IDs: {runStore.RunId}.",
                    discoveryLog.Message);
            });
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
            Assert.False(runDirectory.StartsWith(_workspace.Path, StringComparison.OrdinalIgnoreCase));
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
    public void NoneMode_DoesNotDeleteTemporaryDirectoriesWithOtherNames()
    {
        var otherDirectory = Directory.CreateTempSubdirectory("unrelated-").FullName;
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
    public void ResumeMode_LogsCreatingDatabase()
    {
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardRunStore>(new TestLoggerFactory(testSink, enabled: true));

        using var runStore = new DashboardRunStore(
            CreateOptions($"Create-{Guid.NewGuid():N}", DashboardPersistenceMode.Resume),
            logger,
            TimeProvider.System);

        var creationLog = Assert.Single(
            testSink.Writes,
            write => write.Message == $"Creating dashboard database at '{runStore.DatabasePath}'.");
        Assert.Equal(LogLevel.Debug, creationLog.LogLevel);
    }

    [Fact]
    public void ResumeMode_ReusesApplicationDatabaseWithoutRunSelection()
    {
        var options = CreateOptions("My Dashboard", DashboardPersistenceMode.Resume);
        string firstDatabasePath;

        using (var firstRunStore = CreateRunStore(options))
        {
            firstDatabasePath = firstRunStore.DatabasePath;
            new DashboardSqliteDatabase(firstDatabasePath).InitializeSchema();
        }

        var testSink = new TestSink();
        var logger = new TestLogger<DashboardRunStore>(new TestLoggerFactory(testSink, enabled: true));
        using var secondRunStore = new DashboardRunStore(options, logger, TimeProvider.System);

        Assert.Equal(firstDatabasePath, secondRunStore.DatabasePath);
        Assert.False(secondRunStore.SupportsRunSelection);
        Assert.Collection(secondRunStore.GetRuns(), run => Assert.True(run.IsCurrent));
        Assert.True(DashboardSqliteDatabase.IsCompatible(secondRunStore.DatabasePath));
        var resumeLog = Assert.Single(
            testSink.Writes,
            write => write.Message == $"Resuming dashboard database at '{secondRunStore.DatabasePath}'.");
        Assert.Equal(LogLevel.Debug, resumeLog.LogLevel);
    }

    [Fact]
    public void ResumeMode_DeletesIncompatibleDatabase()
    {
        var options = CreateOptions(persistenceMode: DashboardPersistenceMode.Resume);
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

        var testSink = new TestSink();
        var logger = new TestLogger<DashboardRunStore>(new TestLoggerFactory(testSink, enabled: true));
        using var secondRunStore = new DashboardRunStore(options, logger, TimeProvider.System);

        Assert.Equal(databasePath, secondRunStore.DatabasePath);
        Assert.False(File.Exists(databasePath));
        var incompatibleLog = Assert.Single(
            testSink.Writes,
            write => write.Message == $"Existing dashboard database at '{databasePath}' is incompatible with schema version {DashboardRunStore.SchemaVersion} and will be replaced.");
        Assert.Equal(LogLevel.Information, incompatibleLog.LogLevel);
        new DashboardSqliteDatabase(databasePath).InitializeSchema();
        Assert.True(DashboardSqliteDatabase.IsCompatible(databasePath));
    }

    [Fact]
    public void IsCompatible_ReturnsFalseForMultipleSchemaVersions()
    {
        var databasePath = Path.Combine(_workspace.Path, $"malformed-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL) STRICT; INSERT INTO dashboard_schema VALUES (8), (8);";
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
    public void GetRuns_ReusesLazySnapshot()
    {
        using var runStore = CreateRunStore(CreateOptions());

        var first = runStore.GetRuns();
        var second = runStore.GetRuns();

        Assert.Same(first, second);
    }

    [Fact]
    public void GetRuns_ExcludesRunOwnedByAnotherDashboard()
    {
        var options = CreateOptions();
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var historicalTelemetryRepository = CreateTelemetryRepository(historicalRunStore.DatabasePath, options);
        }

        using var activeRunStore = CreateRunStore(options);
        using var activeTelemetryRepository = CreateTelemetryRepository(activeRunStore.DatabasePath, options);
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
                Assert.Equal(historicalRunId, historicalRun.RunId);
                Assert.NotEqual(activeRunStore.RunId, historicalRun.RunId);
            });
    }

    [Fact]
    public void RunMode_DeletesOldestRunWhenLimitIsExceeded()
    {
        var applicationDirectory = Path.Combine(_workspace.Path, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}"))
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
    public void RunMode_DoesNotDeleteActiveExpiredRun()
    {
        var applicationDirectory = Path.Combine(_workspace.Path, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}"))
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
    public void SelectedHistoricalRun_HoldsLeaseUntilSelectionChanges()
    {
        var options = CreateOptions();
        string historicalRunId;
        string historicalRunDirectory;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            historicalRunDirectory = historicalRunStore.RunDirectory;
            using var historicalTelemetryRepository = CreateTelemetryRepository(historicalRunStore.DatabasePath, options);
        }

        using var currentRunStore = CreateRunStore(options);
        using var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        using var currentResourceRepository = (SqliteResourceRepository)repositoryFactory.CreateResourceRepository(currentDatabase);
        using var dataSource = CreateDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory);
        dataSource.SelectRun(historicalRunId);

        var runsDirectory = Path.GetDirectoryName(historicalRunDirectory)!;
        foreach (var index in Enumerable.Range(1, DashboardRunStore.MaxRuns - 2))
        {
            Directory.CreateDirectory(Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(index):yyyyMMddTHHmmssfffZ}"));
        }

        var deletedRunDirectories = new List<string>();
        using var pruningRunStore = new DashboardRunStore(
            options,
            NullLogger<DashboardRunStore>.Instance,
            TimeProvider.System,
            deletedRunDirectories.Add);

        Assert.Empty(deletedRunDirectories);
        Assert.True(Directory.Exists(historicalRunDirectory));

        dataSource.SelectRun(runId: null);
        using var nextPruningRunStore = new DashboardRunStore(
            options,
            NullLogger<DashboardRunStore>.Instance,
            TimeProvider.System,
            deletedRunDirectories.Add);

        Assert.Equal(historicalRunDirectory, Assert.Single(deletedRunDirectories));
    }

    [Fact]
    public void RunMode_DeleteExpiredRunFails_LogsWarningAndContinues()
    {
        var applicationDirectory = Path.Combine(_workspace.Path, DashboardRunStore.GetApplicationDirectoryName("TestApp"));
        var runsDirectory = Path.Combine(applicationDirectory, "runs");
        var historicalRunDirectories = Enumerable.Range(1, DashboardRunStore.MaxRuns)
            .Select(index => Path.Combine(
                runsDirectory,
                $"{DateTimeOffset.UtcNow.AddDays(-index):yyyyMMddTHHmmssfffZ}"))
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
            TimeProvider.System,
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
    public void GetRuns_DoesNotReadDatabaseSchemaUntilRunIsSelected()
    {
        var options = CreateOptions();
        string incompatibleDatabasePath;
        string incompatibleRunId;

        using (var incompatibleRunStore = CreateRunStore(options))
        {
            incompatibleDatabasePath = incompatibleRunStore.DatabasePath;
            incompatibleRunId = incompatibleRunStore.RunId;
            using var connection = new SqliteConnection($"Data Source={incompatibleDatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (1);";
            command.ExecuteNonQuery();
        }

        using var currentRunStore = CreateRunStore(options);

        Assert.Collection(
            currentRunStore.GetRuns(),
            run => Assert.True(run.IsCurrent),
            run =>
            {
                Assert.Equal(incompatibleRunId, run.RunId);
                Assert.Equal(DashboardRunStore.SchemaVersion, run.SchemaVersion);
            });
        Assert.True(File.Exists(incompatibleDatabasePath));

        using var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        using var currentResourceRepository = (SqliteResourceRepository)repositoryFactory.CreateResourceRepository(currentDatabase);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardDataSource>(new TestLoggerFactory(testSink, enabled: true));
        using var dataSource = CreateDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory, logger);

        var exception = Assert.Throws<InvalidOperationException>(() => dataSource.SelectRun(incompatibleRunId));
        Assert.Equal(
            $"Dashboard database for run '{incompatibleRunId}' does not match run metadata schema version '{DashboardRunStore.SchemaVersion}'.",
            exception.Message);
        var failureLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Warning, failureLog.LogLevel);
        Assert.Equal($"Failed to switch to dashboard run '{incompatibleRunId}'.", failureLog.Message);
        Assert.Same(exception, failureLog.Exception);
    }

    [Fact]
    public void SqliteDatabase_ConfiguresLikeAndForeignKeys()
    {
        var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "connection.db"));
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
            await telemetryRepository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
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
            await ((IResourceRepositoryWriter)resourceRepository).ReplaceResourcesAsync([new Resource
            {
                Name = "api",
                DisplayName = "API",
                ResourceType = "Project",
                CreatedAt = Timestamp.FromDateTime(DateTime.UnixEpoch)
            }]);
        }

        using var currentRunStore = CreateRunStore(options);
        using var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        using var currentResourceRepository = (SqliteResourceRepository)repositoryFactory.CreateResourceRepository(currentDatabase);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardDataSource>(new TestLoggerFactory(testSink, enabled: true));
        using var dataSource = CreateDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory, logger);
        Assert.Empty(dataSource.TelemetryRepository.GetResources());
        Assert.False(dataSource.TelemetryRepository.IsReadOnly);

        dataSource.SelectRun(historicalRunId);

        var switchLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Debug, switchLog.LogLevel);
        Assert.Equal($"Switched dashboard run from '{currentRunStore.RunId}' to '{historicalRunId}'.", switchLog.Message);

        Assert.True(dataSource.IsReadOnly);
        Assert.True(dataSource.TelemetryRepository.IsReadOnly);
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

        using var activitySource = new DashboardActivitySource();
        await using var currentClient = new DashboardClient(
            activitySource,
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
    Assert.False(dataSource.TelemetryRepository.IsReadOnly);

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
        using var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        using var currentResourceRepository = (SqliteResourceRepository)repositoryFactory.CreateResourceRepository(currentDatabase);
        using var dataSource = CreateDataSource(currentRunStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory);
        dataSource.SelectRun("missing");

        Assert.False(dataSource.IsReadOnly);
        Assert.True(dataSource.SelectedRun.IsCurrent);
        Assert.Equal(currentRunStore.RunId, dataSource.SelectedRun.RunId);
        Assert.Same(currentResourceRepository, dataSource.ResourceRepository);
        Assert.Same(currentTelemetryRepository, dataSource.TelemetryRepository);
    }

    [Fact]
    public void UnavailableHistoricalRun_SelectsCurrentRun()
    {
        var options = CreateOptions();
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
        }

        using var currentRunStore = CreateRunStore(options);
        var runStore = new TestDashboardRunStore(currentRunStore.GetRuns(), tryAcquireRunLease: _ => null);
        using var currentDatabase = new DashboardSqliteDatabase(currentRunStore.DatabasePath);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var currentTelemetryRepository = repositoryFactory.CreateTelemetryRepository(currentDatabase);
        using var currentResourceRepository = (SqliteResourceRepository)repositoryFactory.CreateResourceRepository(currentDatabase);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardDataSource>(new TestLoggerFactory(testSink, enabled: true));
        using var dataSource = CreateDataSource(runStore, currentTelemetryRepository, currentResourceRepository, repositoryFactory, logger);

        dataSource.SelectRun(historicalRunId);

        Assert.False(dataSource.IsReadOnly);
        Assert.True(dataSource.SelectedRun.IsCurrent);
        Assert.Equal(currentRunStore.RunId, dataSource.SelectedRun.RunId);
        Assert.Same(currentResourceRepository, dataSource.ResourceRepository);
        Assert.Same(currentTelemetryRepository, dataSource.TelemetryRepository);
        var failureLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Warning, failureLog.LogLevel);
        Assert.Equal($"Failed to switch to dashboard run '{historicalRunId}' because it is no longer available.", failureLog.Message);
    }

    private IOptions<DashboardOptions> CreateOptions(
        string applicationName = "TestApp",
        DashboardPersistenceMode persistenceMode = DashboardPersistenceMode.Run)
    {
        return Options.Create(new DashboardOptions
        {
            ApplicationName = applicationName,
            Data = new DashboardDataOptions
            {
                Directory = _workspace.Path,
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

    private static DashboardRunStore CreateRunStore(IOptions<DashboardOptions> options, TimeProvider? timeProvider = null)
    {
        return new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, timeProvider ?? TimeProvider.System);
    }

    private static DashboardDataSource CreateDataSource(
        IDashboardRunStore runStore,
        ITelemetryRepository currentTelemetryRepository,
        IResourceRepository currentResourceRepository,
        IRepositoryFactory repositoryFactory,
        ILogger<DashboardDataSource>? logger = null)
    {
        return new DashboardDataSource(
            runStore,
            currentTelemetryRepository,
            currentResourceRepository,
            repositoryFactory,
            logger ?? NullLogger<DashboardDataSource>.Instance);
    }

    private RepositoryFactory CreateRepositoryFactory(IOptions<DashboardOptions> options)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(options)
            .AddSingleton<PauseManager>()
            .AddSingleton<IKnownPropertyLookup, MockKnownPropertyLookup>()
            .BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);

        return new RepositoryFactory(serviceProvider);
    }

    public void Dispose()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }

        _workspace.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}