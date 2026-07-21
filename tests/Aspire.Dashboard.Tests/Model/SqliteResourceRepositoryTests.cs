// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SqliteResourceRepositoryTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void Resources_PersistAndReplayWithEquivalentValues()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resource = CreateResource("api-123", "api");

        using (var repository = CreateRepository(workspace.Path))
        {
            var writer = (IResourceRepositoryWriter)repository;
            writer.ReplaceResources([resource]);

            AssertResource(Assert.Single(repository.GetResources()), resource, replicaIndex: 1);

            var updated = resource.Clone();
            updated.State = "Running";
            writer.ApplyChanges([new WatchResourcesChange { Upsert = updated }]);
            Assert.Equal("Running", repository.GetResource(resource.Name)!.State);
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        AssertResource(Assert.Single(historicalRepository.GetResources()), resource, replicaIndex: 1, state: "Running");
    }

    [Fact]
    public async Task ResourceSubscription_ReceivesUpsertAndDelete()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repository = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repository;
        var subscription = await repository.SubscribeResourcesAsync(CancellationToken.None);
        Assert.Empty(subscription.InitialState);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = subscription.Subscription.GetAsyncEnumerator(cts.Token);

        var resource = CreateResource("worker", "worker");
        writer.ApplyChanges([new WatchResourcesChange { Upsert = resource }]);
        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(ResourceViewModelChangeType.Upsert, Assert.Single(enumerator.Current).ChangeType);

        writer.ApplyChanges([new WatchResourcesChange { Delete = new ResourceDeletion { ResourceName = resource.Name } }]);
        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(ResourceViewModelChangeType.Delete, Assert.Single(enumerator.Current).ChangeType);
        Assert.Empty(repository.GetResources());
    }

    [Fact]
    public async Task ResourceSubscription_ReplaceResourcesDeletesOmittedResources()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repository = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repository;
        writer.ReplaceResources([CreateResource("api", "api"), CreateResource("worker", "worker")]);

        var subscription = await repository.SubscribeResourcesAsync(CancellationToken.None);
        Assert.Equal(2, subscription.InitialState.Length);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = subscription.Subscription.GetAsyncEnumerator(cts.Token);

        writer.ReplaceResources([CreateResource("api", "api")]);

        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Collection(
            enumerator.Current,
            change =>
            {
                Assert.Equal(ResourceViewModelChangeType.Delete, change.ChangeType);
                Assert.Equal("worker", change.Resource.Name);
            },
            change =>
            {
                Assert.Equal(ResourceViewModelChangeType.Upsert, change.ChangeType);
                Assert.Equal("api", change.Resource.Name);
            });
    }

    [Fact]
    public async Task ConsoleLogs_UseInsertionOrderAndAllowLineNumbersToRestart()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using (var repository = CreateRepository(workspace.Path))
        {
            var writer = (IResourceRepositoryWriter)repository;
            var insertQueries = CaptureSqlQueries(() => writer.AddConsoleLogs("api", [
                new ConsoleLogLine { LineNumber = 2, Text = "second", IsStdErr = true },
                new ConsoleLogLine { LineNumber = 1, Text = "first" }
            ]));
            var insertQuery = Assert.Single(insertQueries, query => query.TrimStart().StartsWith("INSERT INTO console_logs ", StringComparison.Ordinal));
            Assert.Contains("(@ResourceName, @LineNumber0, @Content0, @IsStdErr0),", insertQuery, StringComparison.Ordinal);
            Assert.Contains("(@ResourceName, @LineNumber1, @Content1, @IsStdErr1)", insertQuery, StringComparison.Ordinal);
            writer.AddConsoleLogs("api", [
                new ConsoleLogLine { LineNumber = 2, Text = "second-updated", IsStdErr = true },
                new ConsoleLogLine { LineNumber = 3, Text = "third" }
            ]);
        }

        using (var restartedRepository = CreateRepository(workspace.Path))
        {
            ((IResourceRepositoryWriter)restartedRepository).AddConsoleLogs(
                "api",
                [new ConsoleLogLine { LineNumber = 1, Text = "first-after-restart" }]);
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        var batches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalRepository.GetConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }
        var lines = Assert.Single(batches);
        Assert.Collection(lines,
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(2, "second", true), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "first", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(3, "third", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "first-after-restart", false), line));
    }

    [Fact]
    public async Task ConsoleLogs_InsertsAreBatched()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repository = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repository;
        var logLines = Enumerable.Range(1, 101)
            .Select(lineNumber => new ConsoleLogLine { LineNumber = lineNumber, Text = $"Line {lineNumber}" })
            .ToArray();

        var queries = CaptureSqlQueries(() => writer.AddConsoleLogs("api", logLines));

        Assert.Equal(2, queries.Count(query => query.TrimStart().StartsWith("INSERT INTO console_logs ", StringComparison.Ordinal)));
        var batches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in repository.GetConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }
        var persistedLines = Assert.Single(batches);
        Assert.Equal(Enumerable.Range(1, 101), persistedLines.Select(line => line.LineNumber));
        Assert.Equal(logLines.Select(line => line.Text), persistedLines.Select(line => line.Content));
    }

    [Fact]
    public void ConsoleLogsLoaded_PersistsWithoutLogLines()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using (var repository = CreateRepository(workspace.Path))
        {
            var writer = (IResourceRepositoryWriter)repository;
            writer.ReplaceResources([CreateResource("api", "api"), CreateResource("worker", "worker")]);
            Assert.False(repository.GetResource("api")!.ConsoleLogsLoaded);

            writer.MarkConsoleLogsLoaded("api");

            var readQueries = CaptureSqlQueries(() => Assert.True(repository.GetResource("api")!.ConsoleLogsLoaded));
            Assert.Empty(readQueries);
            Assert.False(repository.GetResource("worker")!.ConsoleLogsLoaded);

            writer.ApplyChanges([new WatchResourcesChange { Upsert = CreateResource("api", "api") }]);
            Assert.True(repository.GetResource("api")!.ConsoleLogsLoaded);

            writer.ReplaceResources([CreateResource("api", "api"), CreateResource("worker", "worker")]);
            Assert.True(repository.GetResource("api")!.ConsoleLogsLoaded);
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        Assert.True(historicalRepository.GetResource("api")!.ConsoleLogsLoaded);
        Assert.False(historicalRepository.GetResource("worker")!.ConsoleLogsLoaded);
    }

    [Fact]
    public void Resources_AllFieldsAndRecursiveValuesRoundTrip()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var nestedValue = new Value
        {
            StructValue = new Struct
            {
                Fields =
                {
                    ["name"] = Value.ForString("database"),
                    ["values"] = new Value
                    {
                        ListValue = new ListValue
                        {
                            Values =
                            {
                                Value.ForNumber(42.5),
                                Value.ForBool(true),
                                new Value { NullValue = NullValue.NullValue }
                            }
                        }
                    }
                }
            }
        };
        var resource = CreateResource("api-complete", "api");
        resource.State = "Running";
        resource.StateStyle = "success";
        resource.StartedAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddMinutes(1));
        resource.StoppedAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddMinutes(2));
        resource.IsHidden = true;
        resource.SupportsDetailedTelemetry = true;
        resource.IconName = "Box";
        resource.IconVariant = Aspire.DashboardService.Proto.V1.IconVariant.Filled;
        resource.Environment.Add(new EnvironmentVariable { Name = "OPTIONAL", IsFromSpec = true });
        resource.Environment.Add(new EnvironmentVariable { Name = "VALUE", Value = "set" });
        resource.Urls.Add(new Url
        {
            EndpointName = "https",
            FullUrl = "https://api.dev.localhost:5001/path",
            DisplayProperties = new UrlDisplayProperties { SortOrder = 3, DisplayName = "Secure endpoint" }
        });
        resource.Urls.Add(new Url
        {
            EndpointName = "https",
            FullUrl = "https://localhost:5001/path",
            IsInternal = true,
            DisplayProperties = new UrlDisplayProperties { SortOrder = 3, DisplayName = "Secure endpoint" }
        });
        resource.Volumes.Add(new Volume { Source = "data", Target = "/data", MountType = "volume", IsReadOnly = true });
        resource.Relationships.Add(new ResourceRelationship { ResourceName = "database", Type = "Reference" });
        resource.HealthReports.Add(new HealthReport
        {
            Status = HealthStatus.Healthy,
            Key = "ready",
            Description = "Ready",
            Exception = string.Empty,
            LastRunAt = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(30))
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "nested",
            DisplayName = "Nested value",
            Value = nestedValue,
            IsSensitive = true,
            IsHighlighted = true,
            SortOrder = 7
        });
#pragma warning disable CS0612 // ResourceCommand.Parameter must be persisted for compatibility with older AppHosts.
        resource.Commands.Add(new ResourceCommand
        {
            Name = "configure",
            DisplayName = "Configure",
            Parameter = nestedValue.Clone(),
            DisplayDescription = "Configure the resource",
            ConfirmationMessage = "Continue?",
            IsHighlighted = true,
            IconName = "Settings",
            IconVariant = Aspire.DashboardService.Proto.V1.IconVariant.Filled,
            State = ResourceCommandState.Enabled,
            ArgumentInputs =
            {
                new InteractionInput
                {
                    Name = "mode",
                    Label = "Mode",
                    Placeholder = "Select a mode",
                    InputType = InputType.Choice,
                    Required = true,
                    Value = "safe",
                    Description = "Execution mode",
                    EnableDescriptionMarkdown = true,
                    MaxLength = 20,
                    AllowCustomChoice = true,
                    Loading = true,
                    UpdateStateOnChange = true,
                    Disabled = true,
                    MaxFileSize = 1024,
                    AllowMultipleFiles = true,
                    FileFilter = ".json",
                    Options = { ["safe"] = "Safe", ["fast"] = "Fast" },
                    ValidationErrors = { "Choose a mode" }
                }
            }
        });
#pragma warning restore CS0612

        using (var repository = CreateRepository(workspace.Path))
        {
            ((IResourceRepositoryWriter)repository).ReplaceResources([resource]);
        }

        using (var connection = new SqliteConnection($"Data Source={GetDatabasePath(workspace.Path)};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var sqliteCommand = connection.CreateCommand();
            sqliteCommand.CommandText = """
                SELECT COUNT(*)
                FROM dashboard_resource_commands
                WHERE json_extract(parameter_value, '$.name') = 'database';
                """;
            Assert.Equal(1L, sqliteCommand.ExecuteScalar());
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        var actual = Assert.Single(historicalRepository.GetResources());
        Assert.Equal("Running", actual.State);
        Assert.Equal("success", actual.StateStyle);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(1), actual.StartTimeStamp);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(2), actual.StopTimeStamp);
        Assert.True(actual.SupportsDetailedTelemetry);
        Assert.Equal("Box", actual.IconName);
        Assert.Collection(actual.Environment,
            item =>
            {
                Assert.Equal("OPTIONAL", item.Name);
                Assert.Equal(string.Empty, item.Value);
                Assert.True(item.FromSpec);
            },
            item => Assert.Equal("set", item.Value));
        Assert.Equal(nestedValue, actual.Properties["nested"].Value);
        Assert.True(actual.Properties["nested"].IsValueSensitive);
        Assert.Equal(14, actual.Properties["nested"].SortOrder);
        var command = Assert.Single(actual.Commands);
        Assert.Equal("configure", command.Name);
        var input = Assert.Single(command.ArgumentInputs);
        Assert.Equal("Safe", input.Options["safe"]);
        Assert.Equal("Fast", input.Options["fast"]);
        Assert.Equal("Choose a mode", Assert.Single(input.ValidationErrors));
        Assert.Collection(actual.Urls,
            url =>
            {
                Assert.Equal("https", url.EndpointName);
                Assert.Equal("api.dev.localhost", url.Url.Host);
                Assert.False(url.IsInternal);
            },
            url =>
            {
                Assert.Equal("https", url.EndpointName);
                Assert.Equal("localhost", url.Url.Host);
                Assert.True(url.IsInternal);
            });
        Assert.Equal("/data", Assert.Single(actual.Volumes).Target);
        Assert.Equal("database", Assert.Single(actual.Relationships).ResourceName);
        Assert.Equal("ready", Assert.Single(actual.HealthReports).Name);
    }

    [Fact]
    public void Resources_DuplicateEndpointUrlsRoundTrip()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resource = CreateResource("frontend-cqgvshvm", "frontend");
        resource.Urls.AddRange(
        [
            CreateUrl("http", "Online store (http)", "http://frontend-testshop.dev.localhost:5266/"),
            CreateUrl("http", "Online store (http)", "http://localhost:5266/", isInternal: true),
            CreateUrl("https", "Online store (https)", "https://frontend-testshop.dev.localhost:7269/"),
            CreateUrl("https", "Online store (https)", "https://localhost:7269/", isInternal: true),
            CreateUrl("https", "Health", "https://localhost:7269/health", isInternal: true)
        ]);

        using (var repository = CreateRepository(workspace.Path))
        {
            ((IResourceRepositoryWriter)repository).ReplaceResources([resource]);
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        var actual = Assert.Single(historicalRepository.GetResources());
        Assert.Collection(actual.Urls,
            url => AssertUrl(url, "http", "Online store (http)", "http://frontend-testshop.dev.localhost:5266/", isInternal: false),
            url => AssertUrl(url, "http", "Online store (http)", "http://localhost:5266/", isInternal: true),
            url => AssertUrl(url, "https", "Online store (https)", "https://frontend-testshop.dev.localhost:7269/", isInternal: false),
            url => AssertUrl(url, "https", "Online store (https)", "https://localhost:7269/", isInternal: true),
            url => AssertUrl(url, "https", "Health", "https://localhost:7269/health", isInternal: true));

        static void AssertUrl(global::Aspire.Dashboard.Model.UrlViewModel actual, string endpointName, string displayName, string url, bool isInternal)
        {
            Assert.Equal(endpointName, actual.EndpointName);
            Assert.Equal(displayName, actual.DisplayProperties.DisplayName);
            Assert.Equal(url, actual.Url.ToString());
            Assert.Equal(isInternal, actual.IsInternal);
        }
    }

    [Fact]
    public void Resources_BulkLoadKeepsChildRecordsIsolated()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resources = new[]
        {
            CreateResourceWithChildren("api", "API", "api-value"),
            CreateResourceWithChildren("worker", "Worker", "worker-value")
        };

        using (var repository = CreateRepository(workspace.Path))
        {
            ((IResourceRepositoryWriter)repository).ReplaceResources(resources);
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        var actualResources = historicalRepository.GetResources().OrderBy(resource => resource.Name).ToList();
        Assert.Collection(actualResources,
            resource => AssertResourceChildren(resource, "api-value"),
            resource => AssertResourceChildren(resource, "worker-value"));
    }

    [Fact]
    public void Resources_MultipleResourcesArePersistedWithBatchedQueries()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repository = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repository;

        var replaceQueries = CaptureSqlQueries(() => writer.ReplaceResources([
            CreateResourceWithChildren("api", "API", "api-value"),
            CreateResourceWithChildren("worker", "Worker", "worker-value")
        ]));
        AssertBatchedResourceQueries(replaceQueries);

        var applyQueries = CaptureSqlQueries(() => writer.ApplyChanges([
            new WatchResourcesChange { Upsert = CreateResourceWithChildren("api", "API", "api-updated") },
            new WatchResourcesChange { Upsert = CreateResourceWithChildren("worker", "Worker", "worker-updated") }
        ]));
        AssertBatchedResourceQueries(applyQueries);

        var resources = repository.GetResources().OrderBy(resource => resource.Name).ToArray();
        Assert.Collection(resources,
            resource => AssertResourceChildren(resource, "api-updated"),
            resource => AssertResourceChildren(resource, "worker-updated"));
    }

    [Fact]
    public void Schema_HasNoSerializedResourceColumns()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name = 'resources';
            """;
        Assert.Equal(0L, command.ExecuteScalar());

        command.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('dashboard_resources')
            WHERE name = 'payload' OR upper(type) = 'BLOB';
            """;
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Values_AreStoredOnOwnerRowsAsValidatedJson()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var resource = CreateResource("api", "API");
        resource.Properties.Add(new ResourceProperty
        {
            Name = "nested",
            Value = new Value
            {
                StructValue = new Struct
                {
                    Fields =
                    {
                        ["name"] = Value.ForString("database"),
                        ["values"] = new Value
                        {
                            ListValue = new ListValue
                            {
                                Values =
                                {
                                    Value.ForNumber(42.5),
                                    Value.ForBool(true),
                                    new Value { NullValue = NullValue.NullValue }
                                }
                            }
                        }
                    }
                }
            }
        });

        using (var repository = CreateRepository(workspace.Path))
        {
            ((IResourceRepositoryWriter)repository).ReplaceResources([resource]);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM dashboard_resource_properties
            WHERE typeof(value) = 'text'
                AND json_valid(value)
                AND json_extract(value, '$.name') = 'database'
                AND json_array_length(value, '$.values') = 3;
            """;
        Assert.Equal(1L, command.ExecuteScalar());

        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
                AND name IN ('dashboard_values', 'dashboard_value_map_entries', 'dashboard_value_list_items');
            """;
        Assert.Equal(0L, command.ExecuteScalar());

        command.CommandText = "UPDATE dashboard_resource_properties SET value = 'invalid' WHERE resource_name = 'api';";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Schema_ResourceRepositoryInitializesAllEmbeddedScripts()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name IN (
                'dashboard_schema',
                'dashboard_resources',
                'telemetry_logs',
                'telemetry_trace_resources',
                'telemetry_traces',
                'telemetry_metric_instruments')
            ORDER BY name;
            """;

        using var reader = command.ExecuteReader();
        var tableNames = new List<string>();
        while (reader.Read())
        {
            tableNames.Add(reader.GetString(0));
        }

        Assert.Equal(
        [
            "dashboard_resources",
            "dashboard_schema",
            "telemetry_logs",
            "telemetry_metric_instruments",
            "telemetry_trace_resources",
            "telemetry_traces"
        ], tableNames);
    }

    [Fact]
    public void Schema_TraceSummaryIndexesExist()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index' AND name IN (
                'ix_telemetry_spans_parent',
                'ix_telemetry_trace_resources_order')
            ORDER BY name;
            """;

        using var reader = command.ExecuteReader();
        var indexNames = new List<string>();
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }

        Assert.Equal(
        [
            "ix_telemetry_spans_parent",
            "ix_telemetry_trace_resources_order"
        ], indexNames);
    }

    [Fact]
    public void Schema_TelemetryResourceInstanceIdUniquenessPreservesNullAndEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO telemetry_resources (resource_name, instance_id) VALUES ('api', NULL);";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        command.CommandText = "INSERT INTO telemetry_resources (resource_name, instance_id) VALUES ('api', '');";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        command.CommandText = "SELECT COUNT(*) FROM telemetry_resources WHERE resource_name = 'api';";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public void Schema_AllDashboardTablesAreStrict()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_table_list
            WHERE schema = 'main'
                AND type = 'table'
                AND name NOT LIKE 'sqlite_%'
                AND strict = 0
            ORDER BY name;
            """;

        using var reader = command.ExecuteReader();
        var nonStrictTableNames = new List<string>();
        while (reader.Read())
        {
            nonStrictTableNames.Add(reader.GetString(0));
        }

        Assert.Empty(nonStrictTableNames);
    }

    private static string GetDatabasePath(string workspacePath) => Path.Combine(workspacePath, "dashboard.db");

    private static SqliteResourceRepository CreateRepository(string workspacePath, bool readOnly = false)
    {
        return new SqliteResourceRepository(
            GetDatabasePath(workspacePath),
            new MockKnownPropertyLookup(),
            NullLoggerFactory.Instance,
            readOnly);
    }

    private static Resource CreateResource(string name, string displayName)
    {
        return new Resource
        {
            Name = name,
            DisplayName = displayName,
            ResourceType = "Project",
            Uid = $"uid-{name}",
            CreatedAt = Timestamp.FromDateTime(DateTime.UnixEpoch)
        };
    }

    private static Url CreateUrl(string endpointName, string displayName, string url, bool isInternal = false)
    {
        return new Url
        {
            EndpointName = endpointName,
            FullUrl = url,
            IsInternal = isInternal,
            DisplayProperties = new UrlDisplayProperties { DisplayName = displayName }
        };
    }

    private static Resource CreateResourceWithChildren(string name, string displayName, string value)
    {
        var resource = CreateResource(name, displayName);
        resource.Environment.Add(new EnvironmentVariable { Name = "VALUE", Value = value });
        resource.Properties.Add(new ResourceProperty { Name = "property", Value = Value.ForString(value) });
        resource.Commands.Add(new ResourceCommand
        {
            Name = "command",
            DisplayName = "Command",
            ArgumentInputs =
            {
                new InteractionInput
                {
                    Name = "input",
                    Label = "Input",
                    Options = { [value] = value },
                    ValidationErrors = { value }
                }
            }
        });
        return resource;
    }

    private static IReadOnlyList<string> CaptureSqlQueries(Action action)
    {
        var queries = new List<string>();
        using var operation = new Activity("Capture resource persistence queries").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TracingSqliteConnection.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == operation.TraceId && activity.GetTagItem("db.query.text") is string query)
                {
                    queries.Add(query);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        action();

        return queries;
    }

    private static void AssertBatchedResourceQueries(IReadOnlyList<string> queries)
    {
        Assert.Equal(10, queries.Count);

        string[] insertedTables =
        [
            "dashboard_resources",
            "dashboard_resource_environment",
            "dashboard_resource_properties",
            "dashboard_resource_commands",
            "dashboard_resource_command_inputs",
            "dashboard_resource_command_input_options",
            "dashboard_resource_command_input_validation_errors"
        ];

        foreach (var table in insertedTables)
        {
            Assert.Single(queries, query => query.TrimStart().StartsWith($"INSERT INTO {table} ", StringComparison.Ordinal));
        }
    }

    private static void AssertResourceChildren(global::Aspire.Dashboard.Model.ResourceViewModel resource, string expected)
    {
        Assert.Equal(expected, Assert.Single(resource.Environment).Value);
        Assert.Equal(expected, resource.Properties["property"].Value.StringValue);
        var input = Assert.Single(Assert.Single(resource.Commands).ArgumentInputs);
        Assert.Equal(expected, input.Options[expected]);
        Assert.Equal(expected, Assert.Single(input.ValidationErrors));
    }

    private static void AssertResource(global::Aspire.Dashboard.Model.ResourceViewModel actual, Resource expected, int replicaIndex, string? state = null)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.ResourceType, actual.ResourceType);
        Assert.Equal(expected.Uid, actual.Uid);
        Assert.Equal(replicaIndex, actual.ReplicaIndex);
        Assert.Equal(state, actual.State);
    }
}