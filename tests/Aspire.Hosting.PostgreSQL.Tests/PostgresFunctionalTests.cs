// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Net;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Postgres;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Polly;

namespace Aspire.Hosting.PostgreSQL.Tests;

public class PostgresFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnPostgresServerBlocksDependentResources()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // We use the following check added to the Postgres resource to block
        // dependent reosurces from starting. This means we'll have two checks
        // associated with the postgres resource ... the built in one and the
        // one that we add here. We'll manipulate the TCS to allow us to check
        // states at various stages of the execution.
        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var postgres = builder.AddPostgres("postgres")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddPostgres("dependentresource")
                                       .WaitFor(postgres); // Just using another postgres instance as a dependent resource.

        using var app = builder.Build();

        var pendingStart = app.StartAsync();

        // What for the postgres server to start.
        await app.ResourceNotifications.WaitForResourceAsync(postgres.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        // Wait for the dependent resource to be in the Waiting state.
        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        // Now unblock the health check.
        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        // ... and wait for the resource as a whole to move into the health state.
        await app.ResourceNotifications.WaitForResourceHealthyAsync(postgres.Resource.Name).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        // ... then the dependent resource should be able to move into a running state.
        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await pendingStart.DefaultTimeout(TestConstants.LongTimeoutTimeSpan); // Startup should now complete.

        // ... but we'll shut everything down immediately because we are done.
        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyPgAdminResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        IResourceBuilder<PgAdminContainerResource>? adminBuilder = null;
        var redis = builder.AddPostgres("postgres").WithPgAdmin(c => adminBuilder = c);
        Assert.NotNull(adminBuilder);

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Listening at", resourceName: adminBuilder.Resource.Name);

        var client = app.CreateHttpClient(adminBuilder.Resource.Name, "http");

        var path = $"/";
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyPostgresResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1), ShouldHandle = new PredicateBuilder().Handle<NpgsqlException>() })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var postgresDbName = "db1";

        var postgres = builder.AddPostgres("pg");
        var db = postgres.AddDatabase(postgresDbName);

        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();

        hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default)
        });

        hb.AddNpgsqlDataSource(db.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(postgres.Resource.Name, cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
            await connection.OpenAsync(token);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT 1";
            using var results = await command.ExecuteReaderAsync(token);

            Assert.True(results.HasRows);
        }, cts.Token);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithPgWeb()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        IResourceBuilder<PgWebContainerResource>? pgWebBuilder = null;
        var dbName = "postgres";
        var pg = builder.AddPostgres("pg1");
        var db = pg.AddDatabase(dbName);
        pg.WithPgWeb(c =>
        {
            c.WaitFor(pg);
            pgWebBuilder = c;
        });

        Assert.NotNull(pgWebBuilder);

        using var app = builder.Build();

        await app.StartAsync();

        var client = app.CreateHttpClient(pgWebBuilder.Resource.Name, "http");

        await app.ResourceNotifications.WaitForResourceHealthyAsync(pgWebBuilder.Resource.Name).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        var httpContent = new MultipartFormDataContent
        {
            { new StringContent(dbName), "bookmark_id" }
        };

        client.DefaultRequestHeaders.Add("x-session-id", Guid.NewGuid().ToString());

        var response = await client.PostAsync("/api/connect", httpContent);
        var d = await response.Content.ReadAsStringAsync();

        testOutputHelper.WriteLine("RESPONSE: \r\n" + d);

        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var postgresDbName = "tempdb";

        string? volumeName = null;
        string? bindMountPath = null;

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1), ShouldHandle = new PredicateBuilder().Handle<NpgsqlException>() })
            .Build();

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

            var username = "postgres";
            var password = "p@ssw0rd1";

            var usernameParameter = builder1.AddParameter("user", username);
            var passwordParameter = builder1.AddParameter("pwd", password, secret: true);
            var postgres1 = builder1.AddPostgres("pg", usernameParameter, passwordParameter);

            var db1 = postgres1.AddDatabase(postgresDbName);

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(postgres1, nameof(WithDataShouldPersistStateBetweenUsages));

                // If the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                postgres1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "data");

                postgres1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                await app.ResourceNotifications.WaitForResourceHealthyAsync(db1.Resource.Name, cts.Token);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"ConnectionStrings:{db1.Resource.Name}"] = await db1.Resource.ConnectionStringExpression.GetValueAsync(default)
                    });

                    hb.AddNpgsqlDataSource(db1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                            await connection.OpenAsync(token);

                            using var command = connection.CreateCommand();
                            command.CommandText = """
                                CREATE TABLE cars (brand VARCHAR(255));
                                INSERT INTO cars (brand) VALUES ('BatMobile');
                                SELECT * FROM cars;
                            """;

                            using var results = await command.ExecuteReaderAsync(token);

                            Assert.True(results.HasRows);
                        }, cts.Token);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            usernameParameter = builder2.AddParameter("user", username);
            passwordParameter = builder2.AddParameter("pwd", password, secret: true);

            var postgres2 = builder2.AddPostgres("pg", usernameParameter, passwordParameter);
            var db2 = postgres2.AddDatabase(postgresDbName);

            if (useVolume)
            {
                postgres2.WithDataVolume(volumeName);
            }
            else
            {
                postgres2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                await app.ResourceNotifications.WaitForResourceHealthyAsync(db2.Resource.Name, cts.Token);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"ConnectionStrings:{db2.Resource.Name}"] = await db2.Resource.ConnectionStringExpression.GetValueAsync(default)
                    });

                    hb.AddNpgsqlDataSource(db2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                            await connection.OpenAsync(token);

                            using var command = connection.CreateCommand();
                            command.CommandText = $"SELECT * FROM cars;";
                            using var results = await command.ExecuteReaderAsync(token);

                            Assert.True(await results.ReadAsync(token));
                            Assert.Equal("BatMobile", results.GetString("brand"));
                            Assert.False(await results.ReadAsync(token));
                        }, cts.Token);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

        }
        finally
        {
            if (volumeName is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
            }

            if (bindMountPath is not null)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(bindMountPath)!, recursive: true);
                }
                catch
                {
                    // Don't fail test if we can't clean the temporary folder
                }
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithInitBindMount()
    {
        // Creates a script that should be executed when the container is initialized.

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 3, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        var bindMountPath = Directory.CreateTempSubdirectory().FullName;

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode BindMountPermissions =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            File.SetUnixFileMode(bindMountPath, BindMountPermissions);
        }

        try
        {
            File.WriteAllText(Path.Combine(bindMountPath, "init.sql"), """
                CREATE TABLE "Cars" (brand VARCHAR(255));
                INSERT INTO "Cars" (brand) VALUES ('BatMobile');
                """);

            using var builder = TestDistributedApplicationBuilder
                .CreateWithTestContainerRegistry(testOutputHelper);

            var postgresDbName = "db1";
            var postgres = builder.AddPostgres("pg").WithEnvironment("POSTGRES_DB", postgresDbName);

            var db = postgres.AddDatabase(postgresDbName);

#pragma warning disable CS0618 // Type or member is obsolete
            postgres.WithInitBindMount(bindMountPath);
#pragma warning restore CS0618 // Type or member is obsolete

            using var app = builder.Build();

            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default)
            });

            hb.AddNpgsqlDataSource(db.Resource.Name);

            using var host = hb.Build();

            await host.StartAsync();

            await app.ResourceNotifications.WaitForResourceHealthyAsync(db.Resource.Name, cts.Token);

            // Wait until the database is available
            await pipeline.ExecuteAsync(async token =>
            {
                using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                await connection.OpenAsync(token);
                Assert.Equal(ConnectionState.Open, connection.State);
            }, cts.Token);

            await pipeline.ExecuteAsync(async token =>
            {
                using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                await connection.OpenAsync(token);

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"Cars\";";
                using var results = await command.ExecuteReaderAsync(token);

                Assert.True(await results.ReadAsync(token));
                Assert.Equal("BatMobile", results.GetString("brand"));
                Assert.False(await results.ReadAsync(token));
            }, cts.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(bindMountPath, true);
            }
            catch
            {
                // Don't fail test if we can't clean the temporary folder
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithInitFiles()
    {
        // Creates a script that should be executed when the container is initialized.

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 3, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        var initFilesPath = Directory.CreateTempSubdirectory().FullName;

        try
        {
            File.WriteAllText(Path.Combine(initFilesPath, "init.sql"), """
                CREATE TABLE "Cars" (brand VARCHAR(255));
                INSERT INTO "Cars" (brand) VALUES ('BatMobile');
                """);

            using var builder = TestDistributedApplicationBuilder
                .CreateWithTestContainerRegistry(testOutputHelper);

            var postgresDbName = "db1";
            var postgres = builder.AddPostgres("pg")
                                  .WithEnvironment("POSTGRES_DB", postgresDbName)
                                  .WithInitFiles(initFilesPath);

            var db = postgres.AddDatabase(postgresDbName);

            using var app = builder.Build();

            await app.StartAsync();

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default)
            });

            hb.AddNpgsqlDataSource(db.Resource.Name);

            using var host = hb.Build();

            await host.StartAsync();

            await app.ResourceNotifications.WaitForResourceHealthyAsync(db.Resource.Name, cts.Token);

            // Wait until the database is available
            await pipeline.ExecuteAsync(async token =>
            {
                using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                await connection.OpenAsync(token);
                Assert.Equal(ConnectionState.Open, connection.State);
            }, cts.Token);

            await pipeline.ExecuteAsync(async token =>
            {
                using var connection = host.Services.GetRequiredService<NpgsqlConnection>();
                await connection.OpenAsync(token);

                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"Cars\";";
                using var results = await command.ExecuteReaderAsync(token);

                Assert.True(await results.ReadAsync(token));
                Assert.Equal("BatMobile", results.GetString("brand"));
                Assert.False(await results.ReadAsync(token));
            }, cts.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(initFilesPath, true);
            }
            catch
            {
                // Don't fail test if we can't clean the temporary folder
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task Postgres_WithPersistentLifetime_ReusesContainers()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        // Use the same path for both runs
        using var aspireStore = new TestTempDirectory();

        var before = await RunContainersAsync();
        var after = await RunContainersAsync();

        Assert.All(before, Assert.NotNull);
        Assert.All(after, Assert.NotNull);
        Assert.Equal(before, after);

        async Task<string?[]> RunContainersAsync()
        {
            using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper)
                .WithTempAspireStore(aspireStore.Path)
                .WithResourceCleanUp(false);

            var passwordParameter = builder.AddParameter("pwd", "p@ssword1", secret: true);
            builder
                .AddPostgres("resource", password: passwordParameter).WithLifetime(ContainerLifetime.Persistent)
                .WithPgWeb(c => c.WithLifetime(ContainerLifetime.Persistent))
                .WithPgAdmin(c => c.WithLifetime(ContainerLifetime.Persistent))
                .AddDatabase("mydb");

            var app = builder.Build();
            await app.StartAsync(cts.Token);

            var rns = app.Services.GetRequiredService<ResourceNotificationService>();

            var postgresId = await GetContainerIdAsync(rns, "resource", cts.Token);

            var pgWebId = await GetContainerIdAsync(rns, "pgweb", cts.Token);

            var pgadminId = await GetContainerIdAsync(rns, "pgadmin", cts.Token);

            await app.StopAsync(cts.Token).WaitAsync(TimeSpan.FromMinutes(1), cts.Token);

            return [postgresId, pgWebId, pgadminId];
        }

        static async Task<string?> GetContainerIdAsync(ResourceNotificationService rns, string resourceName, CancellationToken cancellationToken)
        {
            await rns.WaitForResourceHealthyAsync(resourceName, cancellationToken);
            var resourceEvent = await rns.WaitForResourceAsync(resourceName, (evt) =>
            {
                return evt.Snapshot.Properties.FirstOrDefault(x => x.Name == "container.id")?.Value != null;
            }, cancellationToken);

            return resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == "container.id")?.Value?.ToString();
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddDatabaseCreatesDatabaseWithCustomScript()
    {
        const string databaseName = "newdb";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var postgres = builder.AddPostgres("pg1");

        var newDb = postgres.AddDatabase(databaseName)
            .WithCreationScript($$"""
                CREATE DATABASE {{databaseName}}
                    ENCODING = 'UTF8';
                """);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddNpgsqlDataSource(newDb.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(newDb.Resource.Name, cts.Token);

        var conn = host.Services.GetRequiredService<NpgsqlConnection>();

        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cts.Token);
        }

        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddDatabaseCreatesDatabaseWithSpecialNames()
    {
        const string databaseName = "!']`'[\"";
        const string resourceName = "db";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var postgres = builder.AddPostgres("pg1");

        var newDb = postgres.AddDatabase(resourceName, databaseName);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddNpgsqlDataSource(newDb.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(newDb.Resource.Name, cts.Token);

        var conn = host.Services.GetRequiredService<NpgsqlConnection>();

        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cts.Token);
        }

        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddDatabaseCreatesDatabaseResiliently()
    {
        // Creating the database multiple times should not fail

        const string databaseName = "db1";
        const string resourceName = "db";

        string? volumeName = null;

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 3, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        var username = "postgres";
        var password = "p@ssw0rd1";

        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

                var usernameParameter = builder.AddParameter("user", username);
                var passwordParameter = builder.AddParameter("pwd", password, secret: true);

                var postgres = builder.AddPostgres("pg1", usernameParameter, passwordParameter);

                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(postgres, nameof(AddDatabaseCreatesDatabaseResiliently));

                if (i == 0)
                {
                    // If the volume already exists (because of a crashing previous run), delete it
                    DockerUtils.AttemptDeleteDockerVolume(volumeName);
                }

                postgres.WithDataVolume(volumeName);

                var newDb = postgres.AddDatabase(resourceName, databaseName);

                using var app = builder.Build();

                await app.StartAsync(cts.Token);

                var hb = Host.CreateApplicationBuilder();
                hb.AddTestLogging(testOutputHelper);

                hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

                hb.AddNpgsqlDataSource(newDb.Resource.Name);

                using var host = hb.Build();

                await host.StartAsync();

                await app.ResourceNotifications.WaitForResourceHealthyAsync(postgres.Resource.Name, cts.Token);

                // Test connection
                await pipeline.ExecuteAsync(async token =>
                {
                    var conn = host.Services.GetRequiredService<NpgsqlConnection>();

                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(token);
                    }

                    Assert.Equal(ConnectionState.Open, conn.State);
                }, cts.Token);

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            if (volumeName is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddDatabaseCreatesMultipleDatabases()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var postgres = builder.AddPostgres("pg1");

        var db1 = postgres.AddDatabase("db1");
        var db2 = postgres.AddDatabase("db2");
        var db3 = postgres.AddDatabase("db3");

        var dbs = new[] { db1, db2, db3 };

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        foreach (var db in dbs)
        {
            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);
            hb.AddKeyedNpgsqlDataSource(db.Resource.Name);
        }

        using var host = hb.Build();

        await host.StartAsync();

        foreach (var db in dbs)
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(db.Resource.Name, cts.Token);

            var conn = host.Services.GetRequiredKeyedService<NpgsqlConnection>(db.Resource.Name);

            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cts.Token);
            }

            Assert.Equal(ConnectionState.Open, conn.State);
        }
    }
}
