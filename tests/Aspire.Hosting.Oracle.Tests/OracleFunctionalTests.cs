// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Polly;

namespace Aspire.Hosting.Oracle.Tests;

[ActiveIssue("https://github.com/microsoft/aspire/issues/5362", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningOnCI))]
public class OracleFunctionalTests(ITestOutputHelper testOutputHelper)
{
    // Folders created for mounted folders need to be granted specific permissions
    // for the non-root user in the container to be able to access them.

    private const UnixFileMode MountFilePermissions =
       UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const string DatabaseReadyText = "Completed: ALTER DATABASE OPEN";

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyEfOracle()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

        var oracleDbName = "freepdb1";

        var oracle = builder.AddOracle("oracle");

        var db = oracle.AddDatabase(oracleDbName);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        await app.WaitForTextAsync(DatabaseReadyText, cancellationToken: cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddOracleDatabaseDbContext<TestDbContext>(db.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

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
    [InlineData(false, Skip = "https://github.com/microsoft/aspire/issues/5191")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var oracleDbName = "freepdb1";

        string? volumeName = null;
        string? bindMountPath = null;

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().HandleResult(false),
                Delay = TimeSpan.FromSeconds(2)
            })
            .Build();

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

            var oracle1 = builder1.AddOracle("oracle");

#pragma warning disable CS0618 // Type or member is obsolete
            var password = oracle1.Resource.PasswordParameter.Value;
#pragma warning restore CS0618 // Type or member is obsolete

            var db1 = oracle1.AddDatabase(oracleDbName);

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(oracle1, nameof(WithDataShouldPersistStateBetweenUsages));

                // If the volume already exists (because of a crashing previous run), try to delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                oracle1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                if (!OperatingSystem.IsWindows())
                {
                    // Change permissions for non-root accounts (container user account)
                    File.SetUnixFileMode(bindMountPath, MountFilePermissions);
                }

                oracle1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync(DatabaseReadyText, cancellationToken: cts.Token);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db1.Resource.Name}"] = await db1.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddOracleDatabaseDbContext<TestDbContext>(db1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        using var dbContext = host.Services.GetRequiredService<TestDbContext>();

                        // Create tables
                        await dbContext.Database.EnsureCreatedAsync(cts.Token);

                        // Seed database
                        dbContext.Cars.Add(new TestDbContext.Car { Brand = "BatMobile" });
                        await dbContext.SaveChangesAsync(cts.Token);

                        await app.StopAsync();

                        // Wait for the database to not be available before attempting to clean the volume.

                        await pipeline.ExecuteAsync(async token =>
                        {
                            return !await dbContext.Database.CanConnectAsync(token);
                        }, cts.Token);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);
            var passwordParameter2 = builder2.AddParameter("pwd", password);

            var oracle2 = builder2.AddOracle("oracle", passwordParameter2);

            var db2 = oracle2.AddDatabase(oracleDbName);

            if (useVolume)
            {
                oracle2.WithDataVolume(volumeName);
            }
            else
            {
                oracle2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync(DatabaseReadyText, cancellationToken: cts.Token);

                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{db2.Resource.Name}"] = await db2.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddOracleDatabaseDbContext<TestDbContext>(db2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        using var dbContext = host.Services.GetRequiredService<TestDbContext>();

                        var brands = await dbContext.Cars.ToListAsync(cancellationToken: cts.Token);
                        Assert.Single(brands);

                        await app.StopAsync();

                        // Wait for the database to not be available before attempting to clean the volume.

                        await pipeline.ExecuteAsync(async token =>
                        {
                            return !await dbContext.Database.CanConnectAsync(token);
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
                    Directory.Delete(bindMountPath, true);
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

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().HandleResult(false),
                Delay = TimeSpan.FromSeconds(2)
            })
            .Build();

        var bindMountPath = Directory.CreateTempSubdirectory().FullName;

        if (!OperatingSystem.IsWindows())
        {
            // Change permissions for non-root accounts (container user account)
            File.SetUnixFileMode(bindMountPath, MountFilePermissions);
        }

        var oracleDbName = "freepdb1";

        try
        {
            File.WriteAllText(Path.Combine(bindMountPath, "01_init.sql"), $"""
                ALTER SESSION SET CONTAINER={oracleDbName};
                ALTER SESSION SET CURRENT_SCHEMA = SYSTEM;
                CREATE TABLE "Cars" ("Id" NUMBER(10) GENERATED BY DEFAULT ON NULL AS IDENTITY NOT NULL, "Brand" NVARCHAR2(2000) NOT NULL, CONSTRAINT "PK_Cars" PRIMARY KEY ("Id") );
                INSERT INTO "Cars" ("Id", "Brand") VALUES (1, 'BatMobile');
                COMMIT;
            """);

            using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

            var oracle = builder.AddOracle("oracle");
            var db = oracle.AddDatabase(oracleDbName);

            var ready = builder;

#pragma warning disable CS0618 // Type or member is obsolete
            oracle.WithInitBindMount(bindMountPath);
#pragma warning restore CS0618 // Type or member is obsolete

            using var app = builder.Build();

            await app.StartAsync();

            await app.WaitForTextAsync(DatabaseReadyText, cancellationToken: cts.Token);

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

            hb.AddOracleDatabaseDbContext<TestDbContext>(db.Resource.Name);

            using var host = hb.Build();

            try
            {
                await host.StartAsync();

                var dbContext = host.Services.GetRequiredService<TestDbContext>();

                // Wait until the database is available
                await pipeline.ExecuteAsync(async token =>
                {
                    return await dbContext.Database.CanConnectAsync(token);
                }, cts.Token);

                var brands = await dbContext.Cars.ToListAsync(cancellationToken: cts.Token);
                Assert.Single(brands);
                Assert.Equal("BatMobile", brands[0].Brand);
            }
            finally
            {
                await app.StopAsync();
            }
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

    [Theory]
    [InlineData(true)]
    [InlineData(false, Skip = "https://github.com/microsoft/aspire/issues/5190")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithInitFiles(bool init)
    {
        // Creates a script that should be executed when the container is initialized.

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().HandleResult(false),
                Delay = TimeSpan.FromSeconds(2)
            })
            .Build();

        var initFilesPath = Directory.CreateTempSubdirectory().FullName;

        var oracleDbName = "freepdb1";

        try
        {
            File.WriteAllText(Path.Combine(initFilesPath, "01_init.sql"), $"""
                ALTER SESSION SET CONTAINER={oracleDbName};
                ALTER SESSION SET CURRENT_SCHEMA = SYSTEM;
                CREATE TABLE "Cars" ("Id" NUMBER(10) GENERATED BY DEFAULT ON NULL AS IDENTITY NOT NULL, "Brand" NVARCHAR2(2000) NOT NULL, CONSTRAINT "PK_Cars" PRIMARY KEY ("Id") );
                INSERT INTO "Cars" ("Id", "Brand") VALUES (1, 'BatMobile');
                COMMIT;
            """);

            using var builder = TestDistributedApplicationBuilder.Create(o => { }, testOutputHelper);

            var oracle = builder.AddOracle("oracle");
            var db = oracle.AddDatabase(oracleDbName);

            var ready = builder;

            if (init)
            {
                oracle.WithInitFiles(initFilesPath);
            }
            else
            {
                oracle.WithDbSetupBindMount(initFilesPath);
            }

            using var app = builder.Build();

            await app.StartAsync();

            await app.WaitForTextAsync(DatabaseReadyText, cancellationToken: cts.Token);

            var hb = Host.CreateApplicationBuilder();

            hb.Configuration[$"ConnectionStrings:{db.Resource.Name}"] = await db.Resource.ConnectionStringExpression.GetValueAsync(default);

            hb.AddOracleDatabaseDbContext<TestDbContext>(db.Resource.Name);

            using var host = hb.Build();

            try
            {
                await host.StartAsync();

                var dbContext = host.Services.GetRequiredService<TestDbContext>();

                // Wait until the database is available
                await pipeline.ExecuteAsync(async token =>
                {
                    return await dbContext.Database.CanConnectAsync(token);
                }, cts.Token);

                var brands = await dbContext.Cars.ToListAsync(cancellationToken: cts.Token);
                Assert.Single(brands);
                Assert.Equal("BatMobile", brands[0].Brand);
            }
            finally
            {
                await app.StopAsync();
            }
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
    public async Task VerifyWaitForOnOracleBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddOracle("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddOracle("dependentresource")
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
}
