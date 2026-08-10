// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SqliteResourceRepositoryTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Resources_PersistAndReplayWithEquivalentValues()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resource = CreateResource("api-123", "api");

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
            await writer.ReplaceResourcesAsync([resource]);

            AssertResource(Assert.Single(repositoryContext.Repository.GetResources()), resource, replicaIndex: 1);

            var updated = resource.Clone();
            updated.State = "Running";
            await writer.ApplyChangesAsync([new WatchResourcesChange { Upsert = updated }]);
            Assert.Equal("Running", repositoryContext.Repository.GetResource(resource.Name)!.State);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        AssertResource(Assert.Single(historicalContext.Repository.GetResources()), resource, replicaIndex: 1, state: "Running");
    }

    [Fact]
    public async Task ResourceSubscription_ReceivesUpsertAndDelete()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repositoryContext = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
        var subscription = await repositoryContext.Repository.SubscribeResourcesAsync(CancellationToken.None);
        Assert.Empty(subscription.InitialState);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = subscription.Subscription.GetAsyncEnumerator(cts.Token);

        var resource = CreateResource("worker", "worker");
        await writer.ApplyChangesAsync([new WatchResourcesChange { Upsert = resource }]);
        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(ResourceViewModelChangeType.Upsert, Assert.Single(enumerator.Current).ChangeType);

        await writer.ApplyChangesAsync([new WatchResourcesChange { Delete = new ResourceDeletion { ResourceName = resource.Name } }]);
        Assert.True(await enumerator.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(ResourceViewModelChangeType.Delete, Assert.Single(enumerator.Current).ChangeType);
        Assert.Empty(repositoryContext.Repository.GetResources());
    }

    [Fact]
    public async Task ResourceSubscription_ReplaceResourcesDeletesOmittedResources()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repositoryContext = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
        await writer.ReplaceResourcesAsync([CreateResource("api", "api"), CreateResource("worker", "worker")]);

        var subscription = await repositoryContext.Repository.SubscribeResourcesAsync(CancellationToken.None);
        Assert.Equal(2, subscription.InitialState.Length);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = subscription.Subscription.GetAsyncEnumerator(cts.Token);

        await writer.ReplaceResourcesAsync([CreateResource("api", "api")]);

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
    public async Task ConsoleLogs_SameProcessReplayIsIgnoredAndLineNumbersCanContinueAfterRestart()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resource = CreateResource("api", "api");
        {
            using var repositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
            await writer.ReplaceResourcesAsync([resource]);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 2, Text = "second", IsStdErr = true },
                new ConsoleLogLine { LineNumber = 1, Text = "first" }
            ]);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 2, Text = "second-updated", IsStdErr = true },
                new ConsoleLogLine { LineNumber = 3, Text = "third" }
            ]);
        }

        {
            using var restartedRepositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)restartedRepositoryContext.Repository).AddConsoleLogsAsync(
                "api",
                [new ConsoleLogLine { LineNumber = 4, Text = "fourth" }]);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var batches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalContext.Repository.GetConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }
        var lines = Assert.Single(batches);
        Assert.Collection(lines,
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(2, "second", true), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "first", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(3, "third", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(4, "fourth", false), line));
    }

    [Fact]
    public async Task ConsoleLogs_ClearSelectedResourcesPersistsAndSuppressesReplay()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        {
            using var repositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
            await writer.ReplaceResourcesAsync([CreateResource("api", "api"), CreateResource("worker", "worker")]);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 1, Text = "api-first" },
                new ConsoleLogLine { LineNumber = 2, Text = "api-second" }
            ]);
            await writer.AddConsoleLogsAsync("worker", [
                new ConsoleLogLine { LineNumber = 1, Text = "worker-first" }
            ]);

            var clearDate = new DateTime(2025, 2, 8, 10, 16, 8, DateTimeKind.Utc);
            await writer.ClearConsoleLogsAsync(["api"], clearDate);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 1, Text = "api-first-replayed" },
                new ConsoleLogLine { LineNumber = 3, Text = "2025-02-08T10:16:08Z api-third-before-clear" },
                new ConsoleLogLine { LineNumber = 4, Text = "2025-02-08T10:16:09Z api-fourth-after-clear" }
            ]);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var apiBatches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalContext.Repository.GetConsoleLogs("api", CancellationToken.None))
        {
            apiBatches.Add(batch);
        }
        Assert.Collection(
            Assert.Single(apiBatches),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(4, "2025-02-08T10:16:09Z api-fourth-after-clear", false), line));

        var workerBatches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalContext.Repository.GetConsoleLogs("worker", CancellationToken.None))
        {
            workerBatches.Add(batch);
        }
        Assert.Collection(
            Assert.Single(workerBatches),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "worker-first", false), line));
    }

    [Fact]
    public async Task ConsoleLogs_ResetLineNumbersAfterRepositoryRestartArePersisted()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resource = CreateResource("api", "api");
        {
            using var repositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
            await writer.ReplaceResourcesAsync([resource]);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 1, Text = "first" },
                new ConsoleLogLine { LineNumber = 2, Text = "second" }
            ]);
        }

        {
            using var restartedRepositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)restartedRepositoryContext.Repository;
            await writer.ReplaceResourcesAsync([resource]);
            await writer.AddConsoleLogsAsync("api", [
                new ConsoleLogLine { LineNumber = 1, Text = "new-first" },
                new ConsoleLogLine { LineNumber = 2, Text = "new-second" },
                new ConsoleLogLine { LineNumber = 3, Text = "new-third" }
            ]);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var batches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalContext.Repository.GetConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Collection(
            Assert.Single(batches),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "first", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(2, "second", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(1, "new-first", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(2, "new-second", false), line),
            line => Assert.Equal(new global::Aspire.Dashboard.Model.ResourceLogLine(3, "new-third", false), line));
    }

    [Fact]
    public async Task ConsoleLogs_LargeBatchRoundTrips()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var logLines = Enumerable.Range(1, 201)
            .Select(lineNumber => new ConsoleLogLine { LineNumber = lineNumber, Text = $"Line {lineNumber}" })
            .ToArray();

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).AddConsoleLogsAsync("api", logLines);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var batches = new List<IReadOnlyList<global::Aspire.Dashboard.Model.ResourceLogLine>>();
        await foreach (var batch in historicalContext.Repository.GetConsoleLogs("api", CancellationToken.None))
        {
            batches.Add(batch);
        }
        var persistedLines = Assert.Single(batches);
        Assert.Equal(Enumerable.Range(1, 201), persistedLines.Select(line => line.LineNumber));
        Assert.Equal(logLines.Select(line => line.Text), persistedLines.Select(line => line.Content));
    }

    [Fact]
    public async Task Resources_LargeBatchRoundTrips()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resources = Enumerable.Range(1, 201)
            .Select(index => CreateResource($"resource-{index}", $"Resource {index}"))
            .ToArray();

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).ReplaceResourcesAsync(resources);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var expected = resources
            .OrderBy(resource => resource.Name)
            .Select(resource => (resource.Name, resource.DisplayName));
        var actual = historicalContext.Repository.GetResources()
            .OrderBy(resource => resource.Name)
            .Select(resource => (resource.Name, resource.DisplayName));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ConsoleLogsLoaded_PersistsWithoutLogLines()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        {
            using var repositoryContext = CreateRepository(workspace.Path);
            var writer = (IResourceRepositoryWriter)repositoryContext.Repository;
            await writer.ReplaceResourcesAsync([CreateResource("api", "api"), CreateResource("worker", "worker")]);
            Assert.False(repositoryContext.Repository.GetResource("api")!.ConsoleLogsLoaded);

            await writer.MarkConsoleLogsLoadedAsync("api");

            var readQueries = await CaptureSqlQueriesAsync(() =>
            {
                Assert.True(repositoryContext.Repository.GetResource("api")!.ConsoleLogsLoaded);
                return Task.CompletedTask;
            });
            Assert.Empty(readQueries);
            Assert.False(repositoryContext.Repository.GetResource("worker")!.ConsoleLogsLoaded);

            await writer.ApplyChangesAsync([new WatchResourcesChange { Upsert = CreateResource("api", "api") }]);
            Assert.True(repositoryContext.Repository.GetResource("api")!.ConsoleLogsLoaded);

            await writer.ReplaceResourcesAsync([CreateResource("api", "api"), CreateResource("worker", "worker")]);
            Assert.True(repositoryContext.Repository.GetResource("api")!.ConsoleLogsLoaded);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        Assert.True(historicalContext.Repository.GetResource("api")!.ConsoleLogsLoaded);
        Assert.False(historicalContext.Repository.GetResource("worker")!.ConsoleLogsLoaded);
    }

    [Fact]
    public async Task Resources_AllFieldsAndRecursiveValuesRoundTrip()
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

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).ReplaceResourcesAsync([resource]);
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

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var actual = Assert.Single(historicalContext.Repository.GetResources());
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
    public async Task Resources_DuplicateEndpointUrlsRoundTrip()
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

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).ReplaceResourcesAsync([resource]);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var actual = Assert.Single(historicalContext.Repository.GetResources());
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
    public async Task Resources_BulkLoadKeepsChildRecordsIsolated()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resources = new[]
        {
            CreateResourceWithChildren("api", "API", "api-value"),
            CreateResourceWithChildren("worker", "Worker", "worker-value")
        };

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).ReplaceResourcesAsync(resources);
        }

        using var historicalContext = CreateRepository(workspace.Path, readOnly: true);
        var actualResources = historicalContext.Repository.GetResources().OrderBy(resource => resource.Name).ToList();
        Assert.Collection(actualResources,
            resource => AssertResourceChildren(resource, "api-value"),
            resource => AssertResourceChildren(resource, "worker-value"));
    }

    [Fact]
    public async Task Resources_MultipleResourcesArePersistedWithBatchedQueries()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repositoryContext = CreateRepository(workspace.Path);
        var writer = (IResourceRepositoryWriter)repositoryContext.Repository;

        var replaceQueries = await CaptureSqlQueriesAsync(() => writer.ReplaceResourcesAsync([
            CreateResourceWithChildren("api", "API", "api-value"),
            CreateResourceWithChildren("worker", "Worker", "worker-value")
        ]));
        AssertBatchedResourceQueries(replaceQueries);

        var applyQueries = await CaptureSqlQueriesAsync(() => writer.ApplyChangesAsync([
            new WatchResourcesChange { Upsert = CreateResourceWithChildren("api", "API", "api-updated") },
            new WatchResourcesChange { Upsert = CreateResourceWithChildren("worker", "Worker", "worker-updated") }
        ]));
        AssertBatchedResourceQueries(applyQueries);

        var resources = repositoryContext.Repository.GetResources().OrderBy(resource => resource.Name).ToArray();
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
    public async Task Values_AreStoredOnOwnerRowsAsValidatedJson()
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

        {
            using var repositoryContext = CreateRepository(workspace.Path);
            await ((IResourceRepositoryWriter)repositoryContext.Repository).ReplaceResourcesAsync([resource]);
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
    public void Schema_TraceSummaryShapeAndIndexesExist()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (CreateRepository(workspace.Path))
        {
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA table_info(telemetry_spans);";
        var spanColumnNames = new List<string>();
        using (var columnReader = command.ExecuteReader())
        {
            while (columnReader.Read())
            {
                spanColumnNames.Add(columnReader.GetString(1));
            }
        }
        Assert.DoesNotContain("resource_order_ticks", spanColumnNames);

        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'telemetry_trace_resources';";
        var traceResourcesSql = Assert.IsType<string>(command.ExecuteScalar());
        Assert.Contains("CHECK (total_spans >= 0)", traceResourcesSql, StringComparison.Ordinal);

        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index' AND name IN (
                'ix_telemetry_spans_parent',
                'ix_telemetry_trace_resources_order')
            ORDER BY name;
            """;

        var indexNames = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                indexNames.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
        [
            "ix_telemetry_spans_parent",
            "ix_telemetry_trace_resources_order"
        ], indexNames);

        command.CommandText = "INSERT INTO telemetry_resources (resource_name) VALUES ('test'); SELECT last_insert_rowid();";
        var resourceId = Assert.IsType<long>(command.ExecuteScalar());
        command.CommandText = """
            INSERT INTO telemetry_traces (
                trace_id, first_span_timestamp_ticks, last_span_end_timestamp_ticks, duration_ticks,
                last_updated_timestamp_ticks, full_name, primary_span_id, has_error, has_gen_ai)
            VALUES ('trace', 1, 2, 1, 2, 'trace', 'span', 0, 0);
            """;
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO telemetry_trace_resources VALUES ('trace', {resourceId}, 1, 0, 0);";
        command.ExecuteNonQuery();

        command.CommandText = "UPDATE telemetry_trace_resources SET total_spans = -1;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "UPDATE telemetry_trace_resources SET errored_spans = -1;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "UPDATE telemetry_trace_resources SET errored_spans = total_spans + 1;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Schema_SpanKindAndStatusLookupsExist()
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
            SELECT kind || ':' || kind_name
            FROM telemetry_span_kinds
            ORDER BY kind;
            """;
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(
            [
                "0:Unspecified",
                "1:Internal",
                "2:Server",
                "3:Client",
                "4:Producer",
                "5:Consumer"
            ], ReadValues(reader));
        }

        command.CommandText = """
            SELECT status || ':' || status_name
            FROM telemetry_span_statuses
            ORDER BY status;
            """;
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(["0:Unset", "1:Ok", "2:Error"], ReadValues(reader));
        }

        command.CommandText = """
            SELECT "table" || ':' || "from" || ':' || "to"
            FROM pragma_foreign_key_list('telemetry_spans')
            WHERE "from" IN ('kind', 'status')
            ORDER BY "from";
            """;
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(
            [
                "telemetry_span_kinds:kind:kind",
                "telemetry_span_statuses:status:status"
            ], ReadValues(reader));
        }

        static List<string> ReadValues(SqliteDataReader reader)
        {
            var values = new List<string>();
            while (reader.Read())
            {
                values.Add(reader.GetString(0));
            }
            return values;
        }
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

    private static SqliteRepositoryTestContext<SqliteResourceRepository> CreateRepository(
        string workspacePath,
        bool readOnly = false)
    {
        return SqliteRepositoryTestHelpers.CreateResourceRepository(
            GetDatabasePath(workspacePath),
            new MockKnownPropertyLookup(),
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

    private static async Task<IReadOnlyList<string>> CaptureSqlQueriesAsync(Func<Task> action)
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

        await action();

        return queries;
    }

    private static void AssertBatchedResourceQueries(IReadOnlyList<string> queries)
    {
        Assert.Equal(9, queries.Count);

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