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

public sealed class DashboardDataSourceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void RunDirectory_IsNestedUnderApplicationDirectoryAndRuns()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, "My Dashboard");

        using var runStore = CreateRunStore(options);

        var applicationDirectoryName = DashboardRunStore.GetApplicationDirectoryName("My Dashboard");
        var expectedRunsDirectory = Path.Combine(workspace.Path, applicationDirectoryName, "runs");
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

    [Theory]
    [InlineData(DashboardPersistenceMode.Run)]
    [InlineData(DashboardPersistenceMode.Resume)]
    public void PersistentApplicationDirectory_HasOwnerOnlyPermissionsOnUnix(DashboardPersistenceMode persistenceMode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var applicationDirectory = DashboardRunStore.GetApplicationDirectory(workspace.Path, "My Dashboard");
        Directory.CreateDirectory(applicationDirectory);
        File.SetUnixFileMode(
            applicationDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        using var runStore = CreateRunStore(CreateOptions(workspace, "My Dashboard", persistenceMode));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(applicationDirectory));
    }

    [Fact]
    public void RunId_IsUtcTimestampWithMillisecondPrecision()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 34, 56, 789, TimeSpan.Zero));

        using var runStore = CreateRunStore(CreateOptions(workspace), timeProvider);

        Assert.Equal("20260720T123456789Z", runStore.RunId);
        Assert.Equal(runStore.RunId, Path.GetFileName(runStore.RunDirectory));
    }

    [Fact]
    public void RunMode_RejectsConcurrentTimestampCollision()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 34, 56, 789, TimeSpan.Zero));
        var options = CreateOptions(workspace);
        using var firstRunStore = CreateRunStore(options, timeProvider);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateRunStore(options, timeProvider));

        Assert.Equal($"Dashboard run '{firstRunStore.RunId}' is already in use by another dashboard process.", exception.Message);
    }

    [Fact]
    public async Task RunMetadata_IsPublishedAfterApplicationStarted()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        using var runStore = CreateRunStore(options);
        var metadataPath = Path.Combine(runStore.RunDirectory, "run.json");
        using var lifetime = new TestHostApplicationLifetime();

        Assert.False(File.Exists(metadataPath));

        using var dataSourcePool = new DashboardDataSourcePool(runStore, CreateRepositoryFactory(options));
        using var initializer = new DashboardDataSourceInitializer(
            dataSourcePool,
            lifetime,
            NullLogger<DashboardDataSourceInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(metadataPath));

        lifetime.NotifyStarted();

        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));

        Assert.Equal(DashboardRunStore.SchemaVersion, metadata.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(DashboardRunStore.SchemaVersion, Assert.Single(runStore.GetRuns()).SchemaVersion);
    }

    [Fact]
    public async Task CurrentRun_PinPersistsWhenRunBecomesHistorical()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        string pinnedRunId;

        using (var currentRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt)))
        {
            await InitializeAndPublishRunAsync(currentRunStore);
            var currentRun = Assert.Single(currentRunStore.GetRuns());
            Assert.False(currentRun.IsPinned);

            currentRunStore.SetRunPinned(currentRun, isPinned: true);

            Assert.True(currentRun.IsPinned);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(currentRunStore.RunDirectory, "run.json")));
            Assert.True(metadata.RootElement.GetProperty("IsPinned").GetBoolean());
            pinnedRunId = currentRun.RunId;
        }

        using var nextRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddMinutes(1)));
        var historicalRun = nextRunStore.GetRuns().Single(run => string.Equals(run.RunId, pinnedRunId, StringComparison.Ordinal));
        Assert.False(historicalRun.IsCurrent);
        Assert.True(historicalRun.IsPinned);
    }

    [Fact]
    public async Task RunMetadata_SchemaInitializationFailure_DoesNotPublishRun()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        using var runStore = CreateRunStore(options);
        using (var connection = new SqliteConnection($"Data Source={runStore.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dashboard_schema (version INTEGER NOT NULL); INSERT INTO dashboard_schema VALUES (1);";
            command.ExecuteNonQuery();
        }
        using var dataSourcePool = new DashboardDataSourcePool(runStore, CreateRepositoryFactory(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => dataSourcePool.InitializeAsync(CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(runStore.RunDirectory, "run.json")));
    }

    [Fact]
    public void ConstructionAndGetRuns_LogResolvedStorageAndDiscoveredRuns()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new TestLoggerProvider(testSink));
        });
        using var runStore = new DashboardRunStore(
            CreateOptions(workspace),
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
    public async Task NoneMode_UsesTemporaryDatabaseAndDeletesItOnDispose()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.None);
        string runDirectory;
        string databasePath;

        using (var runStore = CreateRunStore(options))
        {
            runDirectory = runStore.RunDirectory;
            databasePath = runStore.DatabasePath;
            using var database = new DashboardSqliteDatabase(databasePath, pooling: false);
            await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);

            Assert.False(runStore.SupportsRunSelection);
            Assert.False(runDirectory.StartsWith(workspace.Path, StringComparison.OrdinalIgnoreCase));
            Assert.Collection(runStore.GetRuns(), run => Assert.True(run.IsCurrent));
            Assert.True(File.Exists(databasePath));
        }

        Assert.False(Directory.Exists(runDirectory));
        Assert.False(File.Exists(databasePath));
    }

    [Theory]
    [InlineData(DashboardPersistenceMode.None)]
    [InlineData(DashboardPersistenceMode.Run)]
    [InlineData(DashboardPersistenceMode.Resume)]
    public async Task ServiceProviderDisposal_ReleasesDatabaseAndDeletesUnpublishedDirectory(DashboardPersistenceMode persistenceMode)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, $"Dispose-{Guid.NewGuid():N}", persistenceMode);
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<ILogger<DashboardRunStore>>(NullLogger<DashboardRunStore>.Instance)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<PauseManager>()
            .AddSingleton<IKnownPropertyLookup, MockKnownPropertyLookup>()
            .AddSingleton<DashboardRunStore>()
            .AddSingleton<IDashboardRunStore>(serviceProvider => serviceProvider.GetRequiredService<DashboardRunStore>())
            .AddSingleton<IRepositoryFactory, RepositoryFactory>()
            .AddSingleton<DashboardDataSourcePool>();

        string? runDirectory = null;
        try
        {
            using (var serviceProvider = services.BuildServiceProvider())
            {
                var runStore = serviceProvider.GetRequiredService<DashboardRunStore>();
                // Starting the pool creates its current lease after the run store, so DI disposes the pool first.
                // Schema initialization leaves a physical connection in the SQLite provider pool to be cleared.
                await serviceProvider.GetRequiredService<DashboardDataSourcePool>().InitializeAsync(CancellationToken.None);
                runDirectory = runStore.RunDirectory;
            }

            // None mode owns its temporary directory, and an unpublished Run directory represents a failed startup.
            // Resume mode retains its storage. Deleting it here also verifies that SQLite released the database.
            Assert.Equal(persistenceMode == DashboardPersistenceMode.Resume, Directory.Exists(runDirectory));

            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
            Assert.False(Directory.Exists(runDirectory));
        }
        finally
        {
            if (runDirectory is not null && Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NoneMode_DeletesAbandonedTemporaryDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var abandonedDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-").FullName;
        File.WriteAllText(Path.Combine(abandonedDirectory, "dashboard.db"), string.Empty);

        try
        {
            using var runStore = CreateRunStore(CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.None));

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
    public async Task NoneMode_DoesNotDeleteActiveTemporaryDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.None);
        using var activeRunStore = CreateRunStore(options);
        using var database = new DashboardSqliteDatabase(activeRunStore.DatabasePath, pooling: false);
        await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);

        using var secondRunStore = CreateRunStore(options);

        Assert.True(Directory.Exists(activeRunStore.RunDirectory));
        Assert.True(Directory.Exists(secondRunStore.RunDirectory));
    }

    [Fact]
    public void NoneMode_DoesNotDeleteTemporaryDirectoriesWithOtherNames()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var otherDirectory = Directory.CreateTempSubdirectory("unrelated-").FullName;
        File.WriteAllText(Path.Combine(otherDirectory, "dashboard.db"), string.Empty);

        try
        {
            using var runStore = CreateRunStore(CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.None));

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
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardRunStore>(new TestLoggerFactory(testSink, enabled: true));

        using var runStore = new DashboardRunStore(
            CreateOptions(workspace, $"Create-{Guid.NewGuid():N}", DashboardPersistenceMode.Resume),
            logger,
            TimeProvider.System);

        var creationLog = Assert.Single(
            testSink.Writes,
            write => write.Message == $"Creating dashboard database at '{runStore.DatabasePath}'.");
        Assert.Equal(LogLevel.Debug, creationLog.LogLevel);
    }

    [Fact]
    public async Task ResumeMode_ReusesApplicationDatabaseWithoutRunSelection()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, "My Dashboard", DashboardPersistenceMode.Resume);
        string firstDatabasePath;

        using (var firstRunStore = CreateRunStore(options))
        {
            firstDatabasePath = firstRunStore.DatabasePath;
            using var database = new DashboardSqliteDatabase(firstDatabasePath);
            await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);
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
    public void ResumeMode_RejectsConcurrentDashboardForApplication()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, "My Dashboard", DashboardPersistenceMode.Resume);
        using var firstRunStore = CreateRunStore(options);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateRunStore(options));

        Assert.Equal(
            $"Dashboard data for application 'My Dashboard' is already in use by another dashboard process. Database path: '{firstRunStore.DatabasePath}'.",
            exception.Message);
    }

    [Fact]
    public async Task ResumeMode_DeletesIncompatibleDatabase()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.Resume);
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
        using var database = new DashboardSqliteDatabase(databasePath);
        await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);
        Assert.True(DashboardSqliteDatabase.IsCompatible(databasePath));
    }

    [Fact]
    public void ResumeMode_PreservesDatabaseWhenCompatibilityProbeFails()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace, persistenceMode: DashboardPersistenceMode.Resume);
        string databasePath;

        using (var firstRunStore = CreateRunStore(options))
        {
            databasePath = firstRunStore.DatabasePath;
        }

        var databaseContents = "not a SQLite database"u8.ToArray();
        var walContents = "existing WAL data"u8.ToArray();
        var sharedMemoryContents = "existing shared-memory data"u8.ToArray();
        File.WriteAllBytes(databasePath, databaseContents);
        File.WriteAllBytes($"{databasePath}-wal", walContents);
        File.WriteAllBytes($"{databasePath}-shm", sharedMemoryContents);

        Assert.Throws<SqliteException>(() => CreateRunStore(options));

        Assert.Equal(databaseContents, File.ReadAllBytes(databasePath));
        Assert.True(File.Exists($"{databasePath}-wal"));
        Assert.True(File.Exists($"{databasePath}-shm"));
        // A second probe reaches SQLite instead of failing because the first constructor leaked the run lock.
        Assert.Throws<SqliteException>(() => CreateRunStore(options));
    }

    [Fact]
    public void IsCompatible_ReturnsFalseForMultipleSchemaVersions()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = Path.Combine(workspace.Path, $"malformed-{Guid.NewGuid():N}.db");
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
    public async Task GetRuns_ReturnsCurrentThenCompletedHistoricalRun()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var telemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            historicalRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options);
        using var currentTelemetryContext = await CreateTelemetryRepositoryAsync(currentRunStore.DatabasePath, options);

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
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var runStore = CreateRunStore(CreateOptions(workspace));

        var first = runStore.GetRuns();
        var second = runStore.GetRuns();

        Assert.Same(first, second);
    }

    [Fact]
    public void GetCurrentRunAndGetRunById_ReturnSnapshotDescriptors()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var runStore = CreateRunStore(CreateOptions(workspace));

        var currentRun = runStore.GetCurrentRun();

        Assert.True(currentRun.IsCurrent);
        Assert.Same(currentRun, runStore.GetRunById(currentRun.RunId));
        Assert.Null(runStore.GetRunById("missing"));
    }

    [Fact]
    public async Task GetRuns_ExcludesRunOwnedByAnotherDashboard()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var historicalTelemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            historicalRunStore.PublishRun();
        }

        using var activeRunStore = CreateRunStore(options);
        using var activeTelemetryContext = await CreateTelemetryRepositoryAsync(activeRunStore.DatabasePath, options);
        activeRunStore.PublishRun();
        using var currentRunStore = CreateRunStore(options);
        using var currentTelemetryContext = await CreateTelemetryRepositoryAsync(currentRunStore.DatabasePath, options);

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
    public async Task RunMode_DeletesOldestRunWhenLimitIsExceeded()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 8, 5, 12, 34, 56, TimeSpan.Zero);
        var historicalRunDirectories = new List<string>();

        foreach (var index in Enumerable.Range(1, DashboardRunStore.MaxRuns))
        {
            using var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(-index)));
            historicalRunDirectories.Add(historicalRunStore.RunDirectory);
            await InitializeAndPublishRunWithoutPruningAsync(historicalRunStore);
        }

        using var currentRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt));
        await InitializeAndPublishRunAsync(currentRunStore);

        var runsDirectory = Path.GetDirectoryName(currentRunStore.RunDirectory)!;
        Assert.Equal(DashboardRunStore.MaxRuns, Directory.GetDirectories(runsDirectory).Length);
        Assert.False(Directory.Exists(historicalRunDirectories[^1]));
        Assert.All(historicalRunDirectories[..^1], directory => Assert.True(Directory.Exists(directory)));
        Assert.True(Directory.Exists(currentRunStore.RunDirectory));
    }

    [Fact]
    public async Task RunMode_DoesNotListRunsBeyondRetentionLimitWhenDiscoveredBeforePruning()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 8, 5, 12, 34, 56, TimeSpan.Zero);
        var activeRunCount = 4;

        foreach (var index in Enumerable.Range(0, DashboardRunStore.MaxRuns - activeRunCount))
        {
            using var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddMilliseconds(index)));
            await InitializeAndPublishRunAsync(historicalRunStore);
        }

        var activeRunStores = new List<DashboardRunStore>();
        foreach (var index in Enumerable.Range(DashboardRunStore.MaxRuns - activeRunCount, activeRunCount))
        {
            var activeRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddMilliseconds(index)));
            using var database = new DashboardSqliteDatabase(activeRunStore.DatabasePath, pooling: false);
            await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);
            activeRunStore.PublishRun();
            activeRunStores.Add(activeRunStore);
        }

        using var currentRunStore = CreateRunStore(
            options,
            new FixedTimeProvider(startedAt.AddMilliseconds(DashboardRunStore.MaxRuns)));
        var runsBeforePruning = currentRunStore.GetRuns();

        await InitializeAndPublishRunAsync(currentRunStore);

        foreach (var activeRunStore in activeRunStores)
        {
            activeRunStore.Dispose();
        }

        var prunedRun = Assert.Single(runsBeforePruning, run => run.IsPruned);
        Assert.False(File.Exists(prunedRun.DatabasePath));

        var selectableRuns = currentRunStore.GetRuns();
        Assert.Equal(DashboardRunStore.MaxRuns - activeRunCount, selectableRuns.Count);
        Assert.All(selectableRuns, run => Assert.True(File.Exists(run.DatabasePath)));
    }

    [Fact]
    public async Task RunMode_PinnedRunsDoNotCountTowardHistoricalLimitAndReloadFromMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var pinnedRunIds = new List<string>();
        var pinnedRunDirectories = new List<string>();
        var runIndex = 0;

        for (var index = 0; index < 3; index++)
        {
            string runId;
            using (var runStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(runIndex++))))
            {
                runId = runStore.RunId;
                await InitializeAndPublishRunAsync(runStore);
            }

            using var pinningRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(runIndex++)));
            await InitializeAndPublishRunAsync(pinningRunStore);
            var run = pinningRunStore.GetRuns().Single(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));
            pinningRunStore.SetRunPinned(run, isPinned: true);
            pinnedRunIds.Add(runId);
            pinnedRunDirectories.Add(Path.GetDirectoryName(run.DatabasePath)!);

            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(pinnedRunDirectories[^1], "run.json")));
            Assert.True(metadata.RootElement.GetProperty("IsPinned").GetBoolean());
        }

        for (var index = 0; index < DashboardRunStore.MaxRuns - 1; index++)
        {
            using var runStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(runIndex++)));
            await InitializeAndPublishRunAsync(runStore);
        }

        using var finalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(runIndex)));
        await InitializeAndPublishRunAsync(finalRunStore);
        var runs = finalRunStore.GetRuns();
        Assert.Equal(
            runs.OrderByDescending(run => run.IsCurrent).ThenByDescending(run => run.IsPinned).ThenByDescending(run => run.StartedAtUtc),
            runs);
        Assert.Single(runs, run => run.IsCurrent);
        Assert.Equal(3, runs.Count(run => !run.IsCurrent && run.IsPinned));
        Assert.Equal(DashboardRunStore.MaxRuns - 1, runs.Count(run => !run.IsCurrent && !run.IsPinned));
        Assert.All(pinnedRunIds, runId => Assert.True(finalRunStore.GetRuns().Single(run => string.Equals(run.RunId, runId, StringComparison.Ordinal)).IsPinned));
        Assert.All(pinnedRunDirectories, directory => Assert.True(Directory.Exists(directory)));
    }

    [Fact]
    public async Task RunMode_DoesNotDeleteActiveExpiredRun()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 8, 5, 12, 34, 56, TimeSpan.Zero);

        foreach (var index in Enumerable.Range(1, DashboardRunStore.MaxRuns - 1))
        {
            using var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(-index)));
            await InitializeAndPublishRunWithoutPruningAsync(historicalRunStore);
        }

        using var activeRunStore = CreateRunStore(
            options,
            new FixedTimeProvider(startedAt.AddDays(-DashboardRunStore.MaxRuns)));
        await InitializeAndPublishRunWithoutPruningAsync(activeRunStore);

        using var currentRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt));
        await InitializeAndPublishRunAsync(currentRunStore);

        var runsDirectory = Path.GetDirectoryName(currentRunStore.RunDirectory)!;
        Assert.True(Directory.Exists(activeRunStore.RunDirectory));
        Assert.Equal(DashboardRunStore.MaxRuns + 1, Directory.GetDirectories(runsDirectory).Length);
    }

    [Fact]
    public async Task SelectedHistoricalRun_HoldsLeaseUntilSelectionChanges()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 7, 26, 12, 34, 56, TimeSpan.Zero);
        string historicalRunId;
        string historicalRunDirectory;

        using (var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt)))
        {
            historicalRunId = historicalRunStore.RunId;
            historicalRunDirectory = historicalRunStore.RunDirectory;
            using var historicalTelemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            historicalRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddMilliseconds(1)));
        var repositoryFactory = CreateRepositoryFactory(options);
        using var dataSourcePool = new DashboardDataSourcePool(currentRunStore, repositoryFactory);
        using var dataSource = CreateDataSource(currentRunStore, dataSourcePool);
        dataSource.SelectRun(historicalRunId);

        foreach (var index in Enumerable.Range(1, DashboardRunStore.MaxRuns - 2))
        {
            using var additionalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(index)));
            await InitializeAndPublishRunWithoutPruningAsync(additionalRunStore);
        }

        var deletedRunDirectories = new List<string>();
        using var pruningRunStore = new DashboardRunStore(
            options,
            NullLogger<DashboardRunStore>.Instance,
            new FixedTimeProvider(startedAt.AddMilliseconds(2)),
            deletedRunDirectories.Add);
        await InitializeAndPublishRunAsync(pruningRunStore);

        Assert.Empty(deletedRunDirectories);
        Assert.True(Directory.Exists(historicalRunDirectory));

        dataSource.SelectRun(runId: null);
        using var nextPruningRunStore = new DashboardRunStore(
            options,
            NullLogger<DashboardRunStore>.Instance,
            new FixedTimeProvider(startedAt.AddMilliseconds(3)),
            deletedRunDirectories.Add);
        await InitializeAndPublishRunAsync(nextPruningRunStore);

        Assert.Equal(historicalRunDirectory, Assert.Single(deletedRunDirectories));
    }

    [Fact]
    public async Task SelectedHistoricalRun_SharesDatabaseAcrossDataSources()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var historicalTelemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            historicalRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options);
        var historicalRun = currentRunStore.GetRuns().Single(run => string.Equals(run.RunId, historicalRunId, StringComparison.Ordinal));
        var innerRepositoryFactory = CreateRepositoryFactory(options);
        var repositoryFactory = new RecordingRepositoryFactory(innerRepositoryFactory);
        using var dataSourcePool = new DashboardDataSourcePool(currentRunStore, repositoryFactory);
        using var firstDataSource = CreateDataSource(currentRunStore, dataSourcePool);
        using var secondDataSource = CreateDataSource(currentRunStore, dataSourcePool);

        firstDataSource.SelectRun(historicalRunId);
        secondDataSource.SelectRun(historicalRunId);

        Assert.True(historicalRun.IsLeased);
        var historicalDatabases = repositoryFactory.Databases.Where(database => database.IsReadOnly).ToList();
        var sharedDatabase = Assert.IsType<DashboardSqliteDatabase>(historicalDatabases[0]);
        Assert.Equal(4, historicalDatabases.Count);
        Assert.All(historicalDatabases, database => Assert.Same(sharedDatabase, database));
        Assert.All(historicalDatabases, database => Assert.Same(sharedDatabase.WriteLock, database.WriteLock));
        Assert.Null(currentRunStore.TryAcquireRunLease(historicalRun));

        firstDataSource.SelectRun(runId: null);

        Assert.True(historicalRun.IsLeased);
        Assert.Empty(secondDataSource.TelemetryRepository.GetResources());
        Assert.Null(currentRunStore.TryAcquireRunLease(historicalRun));

        secondDataSource.SelectRun(runId: null);

        Assert.False(historicalRun.IsLeased);
        using (var releasedRunLease = currentRunStore.TryAcquireRunLease(historicalRun))
        {
            Assert.NotNull(releasedRunLease);
            Assert.True(historicalRun.IsLeased);
        }
        Assert.False(historicalRun.IsLeased);
    }

    [Fact]
    public async Task SelectedHistoricalRun_CanBePinned()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var historicalTelemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            historicalRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options);
        var historicalRun = currentRunStore.GetRuns().Single(run => string.Equals(run.RunId, historicalRunId, StringComparison.Ordinal));
        using var dataSourcePool = new DashboardDataSourcePool(currentRunStore, CreateRepositoryFactory(options));
        using var dataSource = CreateDataSource(currentRunStore, dataSourcePool);
        dataSource.SelectRun(historicalRunId);

        Assert.True(historicalRun.IsLeased);
        currentRunStore.SetRunPinned(historicalRun, isPinned: true);

        Assert.True(historicalRun.IsPinned);
        Assert.True(historicalRun.IsLeased);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(historicalRun.DatabasePath)!, "run.json")));
        Assert.True(metadata.RootElement.GetProperty("IsPinned").GetBoolean());
    }

    [Fact]
    public async Task RunMode_DeleteExpiredRunFails_LogsWarningAndContinues()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var startedAt = new DateTimeOffset(2026, 8, 5, 12, 34, 56, TimeSpan.Zero);
        var historicalRunDirectories = new List<string>();

        foreach (var index in Enumerable.Range(1, DashboardRunStore.MaxRuns))
        {
            using var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(startedAt.AddDays(-index)));
            historicalRunDirectories.Add(historicalRunStore.RunDirectory);
            await InitializeAndPublishRunWithoutPruningAsync(historicalRunStore);
        }

        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(testSink)));
        var logger = loggerFactory.CreateLogger<DashboardRunStore>();
        var expiredRunDirectory = historicalRunDirectories[^1];

        using var currentRunStore = new DashboardRunStore(
            options,
            logger,
            new FixedTimeProvider(startedAt),
            directory => throw new IOException($"The directory '{directory}' is in use."));
        await InitializeAndPublishRunAsync(currentRunStore);

        var warning = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Warning, warning.LogLevel);
        Assert.Equal(typeof(DashboardRunStore).FullName, warning.LoggerName);
        Assert.Contains(expiredRunDirectory, warning.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(warning.Exception);
        Assert.True(Directory.Exists(expiredRunDirectory));
        Assert.True(Directory.Exists(currentRunStore.RunDirectory));
    }

    [Fact]
    public void SelectedHistoricalRun_SchemaValidationThrows_ReleasesRunLease()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string malformedDatabasePath;
        string malformedRunId;

        using (var malformedRunStore = CreateRunStore(options))
        {
            malformedDatabasePath = malformedRunStore.DatabasePath;
            malformedRunId = malformedRunStore.RunId;
            using var connection = new SqliteConnection($"Data Source={malformedDatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
            malformedRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options);
        var currentRun = Assert.Single(currentRunStore.GetRuns(), run => run.IsCurrent);
        var malformedRun = currentRun with
        {
            RunId = malformedRunId,
            DatabasePath = malformedDatabasePath,
            IsCurrent = false
        };
        var runStore = new TestDashboardRunStore(
            [currentRun, malformedRun],
            TryOpenTestRunLease);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var dataSourcePool = new DashboardDataSourcePool(runStore, repositoryFactory);
        using var dataSource = CreateDataSource(runStore, dataSourcePool);

        // A run directory whose database was never fully initialized has no dashboard_schema table. That is an
        // incompatible database, not an internal error, so it must not surface the raw SQLite failure.
        var exception = Assert.Throws<InvalidOperationException>(() => dataSource.SelectRun(malformedRunId));
        Assert.Contains("does not match run metadata schema version", exception.Message, StringComparison.Ordinal);

        using var runLease = Assert.IsType<FileStream>(TryOpenTestRunLease(malformedRun));

        var malformedRunDirectory = Path.GetDirectoryName(malformedDatabasePath)!;
        Directory.Delete(malformedRunDirectory, recursive: true);
        Assert.False(Directory.Exists(malformedRunDirectory));
    }

    [Fact]
    public async Task SelectedHistoricalRun_ReplacementValidationThrows_PreservesPreviousSelection()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        using var currentRunStore = CreateRunStore(options);

        var historicalDirectory = Path.Combine(workspace.Path, "historical");
        var historicalDatabasePath = Path.Combine(historicalDirectory, DashboardRunStore.DatabaseFileName);
        using (var historicalTelemetryContext = await CreateTelemetryRepositoryAsync(historicalDatabasePath, options))
        {
        }

        var malformedDirectory = Path.Combine(workspace.Path, "malformed");
        var malformedDatabasePath = Path.Combine(malformedDirectory, DashboardRunStore.DatabaseFileName);
        Directory.CreateDirectory(malformedDirectory);
        using (var connection = new SqliteConnection($"Data Source={malformedDatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }

        var currentRun = Assert.Single(currentRunStore.GetRuns());
        var historicalRun = currentRun with
        {
            RunId = "historical",
            DatabasePath = historicalDatabasePath,
            IsCurrent = false
        };
        var malformedRun = currentRun with
        {
            RunId = "malformed",
            DatabasePath = malformedDatabasePath,
            IsCurrent = false
        };
        var unavailableRun = currentRun with
        {
            RunId = "unavailable",
            DatabasePath = Path.Combine(workspace.Path, "unavailable", DashboardRunStore.DatabaseFileName),
            IsCurrent = false
        };
        var runStore = new TestDashboardRunStore(
            [currentRun, historicalRun, malformedRun, unavailableRun],
            TryOpenTestRunLease);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var dataSourcePool = new DashboardDataSourcePool(runStore, repositoryFactory);
        using var dataSource = CreateDataSource(runStore, dataSourcePool);

        dataSource.SelectRun(historicalRun.RunId);
        var historicalTelemetryRepository = dataSource.TelemetryRepository;
        var historicalResourceRepository = dataSource.ResourceRepository;

        Assert.Throws<InvalidOperationException>(() => dataSource.SelectRun(malformedRun.RunId));

        Assert.Equal(historicalRun, dataSource.SelectedRun);
        Assert.Same(historicalTelemetryRepository, dataSource.TelemetryRepository);
        Assert.Same(historicalResourceRepository, dataSource.ResourceRepository);
        Assert.Null(TryOpenTestRunLease(historicalRun));

        dataSource.SelectRun(unavailableRun.RunId);

        Assert.Equal(historicalRun, dataSource.SelectedRun);
        Assert.Same(historicalTelemetryRepository, dataSource.TelemetryRepository);
        Assert.Same(historicalResourceRepository, dataSource.ResourceRepository);
        Assert.Null(TryOpenTestRunLease(historicalRun));
    }

    [Fact]
    public void SqliteDatabase_ConfiguresLikeAndForeignKeys()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var database = new DashboardSqliteDatabase(Path.Combine(workspace.Path, "connection.db"));
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            Assert.True(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
            Assert.Equal(5, connection.DefaultTimeout);
            Assert.Equal(5, command.CommandTimeout);

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
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options))
        {
            historicalRunId = historicalRunStore.RunId;
            using var telemetryContext = await CreateTelemetryRepositoryAsync(historicalRunStore.DatabasePath, options);
            await telemetryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
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
            using var resourceContext = CreateResourceRepository(historicalRunStore.DatabasePath);
            await ((IResourceRepositoryWriter)resourceContext.Repository).ReplaceResourcesAsync([new Resource
            {
                Name = "api",
                DisplayName = "API",
                ResourceType = "Project",
                CreatedAt = Timestamp.FromDateTime(DateTime.UnixEpoch)
            }]);
            historicalRunStore.PublishRun();
        }

        using var currentRunStore = CreateRunStore(options);
        var repositoryFactory = CreateRepositoryFactory(options);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardDataSource>(new TestLoggerFactory(testSink, enabled: true));
        using var dataSourcePool = new DashboardDataSourcePool(currentRunStore, repositoryFactory);
        using var dataSource = CreateDataSource(currentRunStore, dataSourcePool, logger);
        var currentTelemetryRepository = dataSource.TelemetryRepository;
        var currentResourceRepository = dataSource.ResourceRepository;
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
        Assert.Equal("Test Value!", Assert.Single((await dataSource.TelemetryRepository.GetLogsAsync(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).Items).Message);
        Assert.Empty(currentTelemetryRepository.GetResources());
        Assert.Empty(currentResourceRepository.GetResources());

        var exception = Assert.Throws<InvalidOperationException>(dataSource.EnsureWritable);
        Assert.Equal("Historical dashboard data is read-only.", exception.Message);

        using var activitySource = new DashboardActivitySource();
        await using var currentClient = new DashboardClient(
            activitySource,
            NullLoggerFactory.Instance,
            new ConfigurationManager(),
            options,
            new MockKnownPropertyLookup(),
            new TestStringLocalizer<Resources.Resources>(),
            (IResourceRepositoryWriter)currentResourceRepository);
        IDashboardClient selectedClient = new SelectedDashboardClient(currentClient, dataSource);
        var connectionStateChangedCount = 0;
        selectedClient.ConnectionStateChanged += _ => connectionStateChangedCount++;

        currentClient.SetConnectionStateForTesting(DashboardConnectionState.Disconnected);

        Assert.True(selectedClient.IsEnabled);
        Assert.True(selectedClient.WhenConnected.IsCompletedSuccessfully);
        Assert.Equal(DashboardConnectionState.Connected, selectedClient.ConnectionState);
        Assert.Equal(0, connectionStateChangedCount);
        await selectedClient.ReconnectAsync();
        var clearException = await Assert.ThrowsAsync<InvalidOperationException>(() => selectedClient.ClearConsoleLogsAsync(["api"], DateTime.UtcNow));
        Assert.Equal("Historical dashboard data is read-only.", clearException.Message);

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
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        using var currentRunStore = CreateRunStore(options);
        var repositoryFactory = CreateRepositoryFactory(options);
        using var dataSourcePool = new DashboardDataSourcePool(currentRunStore, repositoryFactory);
        using var dataSource = CreateDataSource(currentRunStore, dataSourcePool);
        var currentTelemetryRepository = dataSource.TelemetryRepository;
        var currentResourceRepository = dataSource.ResourceRepository;
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
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var options = CreateOptions(workspace);
        var historicalRunTime = new DateTimeOffset(2026, 7, 20, 12, 34, 56, TimeSpan.Zero);
        string historicalRunId;

        using (var historicalRunStore = CreateRunStore(options, new FixedTimeProvider(historicalRunTime)))
        {
            historicalRunId = historicalRunStore.RunId;
        }

        using var currentRunStore = CreateRunStore(options, new FixedTimeProvider(historicalRunTime.AddMilliseconds(1)));
        var runStore = new TestDashboardRunStore(currentRunStore.GetRuns(), tryAcquireRunLease: _ => null);
        var repositoryFactory = CreateRepositoryFactory(options);
        var testSink = new TestSink();
        var logger = new TestLogger<DashboardDataSource>(new TestLoggerFactory(testSink, enabled: true));
        using var dataSourcePool = new DashboardDataSourcePool(runStore, repositoryFactory);
        using var dataSource = CreateDataSource(runStore, dataSourcePool, logger);
        var currentTelemetryRepository = dataSource.TelemetryRepository;
        var currentResourceRepository = dataSource.ResourceRepository;

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

    private static IOptions<DashboardOptions> CreateOptions(
        TemporaryWorkspace workspace,
        string applicationName = "TestApp",
        DashboardPersistenceMode persistenceMode = DashboardPersistenceMode.Run)
    {
        return Options.Create(new DashboardOptions
        {
            ApplicationName = applicationName,
            Data = new DashboardDataOptions
            {
                Directory = workspace.Path,
                PersistenceMode = persistenceMode
            }
        });
    }

    private static async Task<SqliteRepositoryTestContext<SqliteTelemetryRepository>> CreateTelemetryRepositoryAsync(
        string databasePath,
        IOptions<DashboardOptions> options)
    {
        var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(
            databasePath,
            pooling: true,
            dashboardOptions: options);
        return context;
    }

    private static async Task InitializeAndPublishRunAsync(DashboardRunStore runStore)
    {
        await InitializeAndPublishRunWithoutPruningAsync(runStore);

        // Production defers pruning until the host has started so slow file system work stays off the startup
        // path. Tests run both steps here so they observe the same end state.
        runStore.PruneExpiredRuns();
    }

    private static async Task InitializeAndPublishRunWithoutPruningAsync(DashboardRunStore runStore)
    {
        using var database = new DashboardSqliteDatabase(runStore.DatabasePath, pooling: false);
        await database.InitializeSchemaAsync(cancellationToken: CancellationToken.None);
        runStore.PublishRun();
    }

    private static SqliteRepositoryTestContext<SqliteResourceRepository> CreateResourceRepository(
        string databasePath)
    {
        var context = SqliteRepositoryTestHelpers.CreateResourceRepository(
            databasePath,
            new MockKnownPropertyLookup(),
            pooling: true);
        return context;
    }

    private static DashboardRunStore CreateRunStore(IOptions<DashboardOptions> options, TimeProvider? timeProvider = null)
    {
        return new DashboardRunStore(options, NullLogger<DashboardRunStore>.Instance, timeProvider ?? TimeProvider.System);
    }

    private static DashboardDataSource CreateDataSource(
        IDashboardRunStore runStore,
        DashboardDataSourcePool dataSourcePool,
        ILogger<DashboardDataSource>? logger = null)
    {
        dataSourcePool.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        return new DashboardDataSource(
            runStore,
            logger ?? NullLogger<DashboardDataSource>.Instance,
            dataSourcePool);
    }

    private static FileStream? TryOpenTestRunLease(DashboardRunDescriptor run)
    {
        var runDirectory = Path.GetDirectoryName(run.DatabasePath)!;
        try
        {
            var runLock = new FileStream(
                DashboardRunStore.GetRunLockPath(runDirectory),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            if (!Directory.Exists(runDirectory))
            {
                runLock.Dispose();
                return null;
            }

            return runLock;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RepositoryFactory CreateRepositoryFactory(IOptions<DashboardOptions> options)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(options)
            .AddSingleton<PauseManager>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IKnownPropertyLookup, MockKnownPropertyLookup>()
            .BuildServiceProvider();

        return new RepositoryFactory(serviceProvider);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingRepositoryFactory(IRepositoryFactory inner) : IRepositoryFactory
    {
        public List<DashboardSqliteDatabase> Databases { get; } = [];

        public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database)
        {
            Databases.Add(database);
            return inner.CreateTelemetryRepository(database);
        }

        public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database)
        {
            Databases.Add(database);
            return inner.CreateResourceRepository(database);
        }
    }
}