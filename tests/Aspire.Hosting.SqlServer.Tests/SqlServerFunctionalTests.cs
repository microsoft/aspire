// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Polly;

namespace Aspire.Hosting.SqlServer.Tests;

public class SqlServerFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnSqlServerBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddSqlServer("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddSqlServer("dependentresource")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifySqlServerResource()
    {
        const string databaseName = "newdb";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = int.MaxValue, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

        var sqlserver = builder.AddSqlServer("sqlserver");
        var newDb = sqlserver.AddDatabase(databaseName);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddSqlServerDbContext<TestDbContext>(newDb.Resource.Name);
        hb.AddSqlServerClient(newDb.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        // Test SqlConnection
        await pipeline.ExecuteAsync(async token =>
        {
            // the connection is scoped, don't dispose if between retries
            var conn = host.Services.GetRequiredService<SqlConnection>();

            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(token);
            }

            using var selectCommand = conn.CreateCommand();
            selectCommand.CommandText = $"SELECT 1";
            using var results = await selectCommand.ExecuteReaderAsync(token);

            Assert.True(results.HasRows);
        }, cts.Token);

        var dbContext = host.Services.GetRequiredService<TestDbContext>();

        await dbContext.Database.EnsureCreatedAsync(cts.Token);
        dbContext.Cars.Add(new TestDbContext.Car { Brand = "BatMobile" });
        await dbContext.SaveChangesAsync(cts.Token);
        var cars = await dbContext.Cars.ToListAsync(cts.Token);

        Assert.Single(cars);
        Assert.Equal("BatMobile", cars[0].Brand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        const string databaseName = "db";

        string? volumeName = null;
        string? bindMountPath = null;

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = int.MaxValue, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

            var sqlserver1 = builder1.AddSqlServer("sqlserver");
            var db1 = sqlserver1.AddDatabase(databaseName);

#pragma warning disable CS0618 // Type or member is obsolete
            var password = sqlserver1.Resource.PasswordParameter.Value;
#pragma warning restore CS0618 // Type or member is obsolete

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(sqlserver1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                sqlserver1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                if (!OperatingSystem.IsWindows())
                {
                    // The docker container runs as a non-root user, so we need to grant other user's read/write permission
                    // to the bind mount directory.
                    // Note that we need to do this after creating the directory, because the umask is applied at the time of creation.
                    const UnixFileMode BindMountPermissions =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

                    File.SetUnixFileMode(bindMountPath, BindMountPermissions);
                }

                sqlserver1.WithDataBindMount(bindMountPath);
            }

            using var app1 = builder1.Build();

            await app1.StartAsync();

            await app1.ResourceNotifications.WaitForResourceHealthyAsync(db1.Resource.Name, cts.Token);

            try
            {
                var hb1 = Host.CreateApplicationBuilder();
                hb1.AddTestLogging(testOutputHelper);

                hb1.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{db1.Resource.Name}"] = await db1.Resource.ConnectionStringExpression.GetValueAsync(default),
                });

                hb1.AddSqlServerClient(db1.Resource.Name);

                using var host1 = hb1.Build();

                await host1.StartAsync();

                await pipeline.ExecuteAsync(async token =>
                {
                    // the connection is scoped, don't dispose if between retries
                    var conn = host1.Services.GetRequiredService<SqlConnection>();

                    if (conn.State != ConnectionState.Open)
                    {
                        await conn.OpenAsync(token);
                    }

                    using var command = conn.CreateCommand();
                    command.CommandText = """
                        DROP TABLE IF EXISTS [Cars];
                        CREATE TABLE [Cars] ([Brand] nvarchar(max) NOT NULL);
                        INSERT INTO [Cars] ([Brand]) VALUES ('BatMobile');
                        SELECT * FROM [Cars];
                    """;

                    using var results = await command.ExecuteReaderAsync(token);

                    Assert.True(results.HasRows);
                }, cts.Token);

                await app1.StopAsync();

                await pipeline.ExecuteAsync(async token =>
                {
                    // the connection is scoped, don't dispose if between retries
                    var conn = host1.Services.GetRequiredService<SqlConnection>();

                    try
                    {
                        await conn.OpenAsync(token);
                    }
                    catch
                    {
                        // Failing means the database is correctly down
                        return;
                    }

                    Assert.Fail("Waiting for database to be down");
                }, cts.Token);
            }
            finally
            {
                // Stops the container, or the Volume/mount would still be in use
                await app1.StopAsync();
            }

            using var builder2 = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);
            var passwordParameter2 = builder2.AddParameter("pwd", password);

            var sqlserver2 = builder2.AddSqlServer("sqlserver2", passwordParameter2);
            var db2 = sqlserver2.AddDatabase(databaseName);

            if (useVolume)
            {
                sqlserver2.WithDataVolume(volumeName);
            }
            else
            {
                sqlserver2.WithDataBindMount(bindMountPath!);
            }

            using (var app2 = builder2.Build())
            {
                await app2.StartAsync();

                await app2.ResourceNotifications.WaitForResourceHealthyAsync(db2.Resource.Name, cts.Token);

                try
                {
                    var hb2 = Host.CreateApplicationBuilder();
                    hb2.AddTestLogging(testOutputHelper);

                    hb2.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"ConnectionStrings:{db2.Resource.Name}"] = await db2.Resource.ConnectionStringExpression.GetValueAsync(default),
                    });

                    hb2.AddSqlServerClient(db2.Resource.Name);

                    using (var host2 = hb2.Build())
                    {
                        await host2.StartAsync();

                        await pipeline.ExecuteAsync(async token =>
                        {
                            // the connection is scoped, don't dispose if between retries
                            var conn = host2.Services.GetRequiredService<SqlConnection>();

                            if (conn.State != ConnectionState.Open)
                            {
                                await conn.OpenAsync(token);
                            }
                        }, cts.Token);

                        var conn = host2.Services.GetRequiredService<SqlConnection>();

                        using var command = conn.CreateCommand();
                        command.CommandText = $"SELECT * FROM [Cars];";
                        using var results = await command.ExecuteReaderAsync(cts.Token);

                        Assert.True(await results.ReadAsync(cts.Token));
                        Assert.Equal("BatMobile", results.GetString(0));
                        Assert.False(await results.ReadAsync(cts.Token));
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app2.StopAsync();
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
                    Directory.Delete(bindMountPath, recursive: true);
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
    public async Task AddDatabaseCreatesDatabaseWithCustomScript()
    {
        const string databaseName = "newdb";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

        var sqlserver = builder.AddSqlServer("sqlserver");

        // Create a database with Accent Insensitive collation
        var newDb = sqlserver.AddDatabase(databaseName)
            .WithCreationScript($$"""
                CREATE DATABASE [{{databaseName}}] COLLATE French_CI_AI;
                GO

                USE [{{databaseName}}];
                GO

                CREATE TABLE [Modèles] ([Name] nvarchar(max) NOT NULL);
                INSERT INTO [Modèles] ([Name]) VALUES ('BatMobile');
                GO

                """);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddSqlServerClient(newDb.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(newDb.Resource.Name, cts.Token);

        // Test SqlConnection
        await pipeline.ExecuteAsync(async token =>
        {
            // the connection is scoped, don't dispose if between retries
            var conn = host.Services.GetRequiredService<SqlConnection>();

            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(token);
            }

            using var selectCommand = conn.CreateCommand();
            selectCommand.CommandText = "SELECT * FROM [Modeles]; -- Incorrect accent to verify the database collation";

            using var results = await selectCommand.ExecuteReaderAsync(token);
            Assert.True(results.HasRows);
        }, cts.Token);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddDatabaseCreatesDatabaseWithSpecialNames()
    {
        const string databaseName = "!'][\"";
        const string resourceName = "db";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 3, BackoffType = DelayBackoffType.Linear, Delay = TimeSpan.FromSeconds(2) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

        var sqlserver = builder.AddSqlServer("sqlserver");

        var newDb = sqlserver.AddDatabase(resourceName, databaseName);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddSqlServerClient(newDb.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(newDb.Resource.Name, cts.Token);

        // Test SqlConnection
        await pipeline.ExecuteAsync(async token =>
        {
            // the connection is scoped, don't dispose if between retries
            var conn = host.Services.GetRequiredService<SqlConnection>();

            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(token);
            }

            Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        }, cts.Token);
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

        var password = "p@ssw0rd1";

        try
        {
            for (var i = 0; i < 2; i++)
            {
                using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

                var passwordParameter = builder.AddParameter("pwd", password, secret: true);

                var sqlserver = builder.AddSqlServer("db1", passwordParameter);

                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(sqlserver, nameof(AddDatabaseCreatesDatabaseResiliently));

                if (i == 0)
                {
                    // If the volume already exists (because of a crashing previous run), delete it
                    DockerUtils.AttemptDeleteDockerVolume(volumeName);
                }

                sqlserver.WithDataVolume(volumeName);

                var newDb = sqlserver.AddDatabase(resourceName, databaseName);

                using var app = builder.Build();

                await app.StartAsync(cts.Token);

                var hb = Host.CreateApplicationBuilder();
                hb.AddTestLogging(testOutputHelper);

                hb.Configuration[$"ConnectionStrings:{newDb.Resource.Name}"] = await newDb.Resource.ConnectionStringExpression.GetValueAsync(default);

                hb.AddSqlServerClient(newDb.Resource.Name);

                using var host = hb.Build();

                await host.StartAsync();

                await app.ResourceNotifications.WaitForResourceHealthyAsync(sqlserver.Resource.Name, cts.Token);

                // Test connection
                await pipeline.ExecuteAsync(async token =>
                {
                    // the connection is scoped, don't dispose if between retries
                    var conn = host.Services.GetRequiredService<SqlConnection>();

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

        using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

        var sqlserver = builder.AddSqlServer("sqlserver");

        var db1 = sqlserver.AddDatabase("db1");
        var db2 = sqlserver.AddDatabase("db2");
        var db3 = sqlserver.AddDatabase("db3");

        var dbs = new[] { db1, db2, db3 };

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        foreach (var db in dbs)
        {
            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);
            hb.AddKeyedSqlServerClient(db.Resource.Name);
        }

        using var host = hb.Build();

        await host.StartAsync();

        foreach (var db in dbs)
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(db.Resource.Name, cts.Token);

            var conn = host.Services.GetRequiredKeyedService<SqlConnection>(db.Resource.Name);

            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(cts.Token);
            }

            Assert.Equal(ConnectionState.Open, conn.State);
        }
    }
}
