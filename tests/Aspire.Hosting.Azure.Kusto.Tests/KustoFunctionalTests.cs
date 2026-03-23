// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Kusto.Cloud.Platform.Data;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Exceptions;
using Kusto.Data.Net.Client;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Polly;

namespace Aspire.Hosting.Azure.Kusto.Tests;

public class KustoFunctionalTests
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly ResiliencePipeline _resiliencePipeline;

    public KustoFunctionalTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;

        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder().Handle<KustoRequestThrottledException>().Handle<RetryableTestException>(),
            })
            .Build();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/dotnet/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task KustoEmulator_Starts()
    {
        using var timeout = new CancellationTokenSource(TestConstants.ExtraLongTimeoutTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(kusto.Resource.Name, cancellationToken: cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(_testOutputHelper);
        hb.Configuration["ConnectionStrings:KustoConnection"] = await kusto.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
        hb.Services.AddSingleton<ICslQueryProvider>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("KustoConnection") ?? throw new ArgumentException("Connection string for Kusto is not set in configuration.");
            var kcsb = new KustoConnectionStringBuilder(connectionString);

            return KustoClientFactory.CreateCslQueryProvider(kcsb);
        });

        using var host = hb.Build();
        await host.StartAsync(cts.Token);

        var client = host.Services.GetRequiredService<ICslQueryProvider>();

        var result = await _resiliencePipeline.ExecuteAsync(async cancellationToken => await ExecuteQueryAsync(client, cancellationToken), cts.Token);
        Assert.Equal("Hello, World!", result);

        static async Task<string?> ExecuteQueryAsync(ICslQueryProvider client, CancellationToken cancellationToken)
        {
            using var reader = await client.ExecuteQueryAsync(client.DefaultDatabaseName, "print message = \"Hello, World!\"", new ClientRequestProperties(), cancellationToken);
            if (reader.Read())
            {
                return reader["message"].ToString();
            }
            else
            {
                throw new RetryableTestException("Expected to read a row from the Kusto query, but no rows were returned.");
            }
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/dotnet/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task KustoEmulator_WithDatabase_CanReadIngestedData()
    {
        using var timeout = new CancellationTokenSource(TestConstants.ExtraLongTimeoutTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
        var kustoDb = kusto.AddReadWriteDatabase("TestDb");

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(kustoDb.Resource.Name, cancellationToken: cts.Token);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(_testOutputHelper);
        hb.Configuration["ConnectionStrings:KustoTestDbConnection"] = await kustoDb.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
        hb.Services.AddSingleton<ICslQueryProvider>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("KustoTestDbConnection") ?? throw new ArgumentException("Connection string for Kusto is not set in configuration.");
            var kcsb = new KustoConnectionStringBuilder(connectionString);

            return KustoClientFactory.CreateCslQueryProvider(kcsb);
        });
        hb.Services.AddSingleton<ICslAdminProvider>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("KustoTestDbConnection") ?? throw new ArgumentException("Connection string for Kusto TestDb is not set in configuration.");
            var kcsb = new KustoConnectionStringBuilder(connectionString);
            return KustoClientFactory.CreateCslAdminProvider(kcsb);
        });

        using var host = hb.Build();
        await host.StartAsync(cts.Token);

        var admin = host.Services.GetRequiredService<ICslAdminProvider>();
        await _resiliencePipeline.ExecuteAsync(async cancellationToken => await SeedDataAsync(admin), cts.Token);

        var client = host.Services.GetRequiredService<ICslQueryProvider>();

        var results = await _resiliencePipeline.ExecuteAsync(async cancellationToken => await ReadDataAsync(client, cancellationToken), cts.Token);

        await Verify(results)
            .DontScrubDateTimes();

        static async Task SeedDataAsync(ICslAdminProvider provider)
        {
            const string command =
            """
            .execute database script with (ThrowOnErrors=true) <|
                .create-merge table TestTable (Id: int, Name: string, Timestamp: datetime)
                .ingest inline into table TestTable <|
                    1,"Alice",datetime(2024-01-01T10:00:00Z)
                    2,"Bob",datetime(2024-01-01T11:00:00Z)
                    3,"Charlie",datetime(2024-01-01T12:00:00Z)
            """;

            await provider.ExecuteControlCommandAsync(provider.DefaultDatabaseName, command, new ClientRequestProperties());
        }

        static async Task<List<object[]>> ReadDataAsync(ICslQueryProvider client, CancellationToken cancellationToken)
        {
            using var reader = await client.ExecuteQueryAsync(client.DefaultDatabaseName, "TestTable", new ClientRequestProperties(), cancellationToken);
            var results = reader.ToEnumerableObjectArray().ToList();

            if (results.Count == 0)
            {
                throw new RetryableTestException("Expected to read rows from the Kusto query, but no rows were returned.");
            }

            return results;
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/dotnet/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task KustoEmulator_WithDatabaseThatAlreadyExists_ErrorIsIgnored()
    {
        using var timeout = new CancellationTokenSource(TestConstants.ExtraLongTimeoutTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);
        builder.Services.AddFakeLogging();

        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
        kusto.AddReadWriteDatabase("TestDb1", "TestDb");
        kusto.AddReadWriteDatabase("TestDb2", "TestDb");

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(kusto.Resource.Name, cts.Token);

        // Assert no warnings or errors were logged about the database already existing
        var snapshot = app.Services.GetRequiredService<FakeLogCollector>().GetSnapshot();
        var logs = snapshot.Where(IsResourceLog).Where(record => record.Level >= LogLevel.Warning);
        Assert.Empty(logs);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/dotnet/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task KustoEmulator_WithInvalidDatabase_LogsErrorAndContinues()
    {
        using var timeout = new CancellationTokenSource(TestConstants.ExtraLongTimeoutTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);
        builder.Services.AddFakeLogging();

        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
        var db1 = kusto.AddReadWriteDatabase("TestDb1", "TestDb");
        var db2 = kusto.AddReadWriteDatabase("TestDb2", "__invalid");

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(kusto.Resource.Name, cts.Token);

        // Assert the valid database is healthy and the invalid database failed to start
        await rns.WaitForResourceHealthyAsync(db1.Resource.Name, cts.Token);
        await rns.WaitForResourceAsync(db2.Resource.Name, KnownResourceStates.FailedToStart, cts.Token);

        // Assert an error was logged about the invalid database
        var snapshot = app.Services.GetRequiredService<FakeLogCollector>().GetSnapshot();
        var logs = snapshot.Where(IsResourceLog).Where(record => record.Level >= LogLevel.Warning);
        Assert.Single(logs);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/dotnet/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task KustoEmulator_WithBindMount_IsUsedForPersistence()
    {
        using var timeout = new CancellationTokenSource(TestConstants.ExtraLongTimeoutTimeSpan);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        using var temp = new TestTempDirectory();

        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        const string dbName = "TestDb";
        const string dbPath = "/kustodata/";
        var script = AzureKustoEmulatorContainerDefaults.DefaultCreateDatabaseCommand(dbName, dbPath);
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator(configureContainer: container =>
        {
            container.WithBindMount(temp.Path, dbPath);
        });
        var kustoDb = kusto.AddReadWriteDatabase(dbName).WithCreationScript(script);

        // Ensure the directory is empty before starting the application
        Assert.Empty(GetFilesInMount());

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(kustoDb.Resource.Name, cts.Token);

        // Ensure the directory has dbs
        Assert.NotEmpty(GetFilesInMount());

        string[] GetFilesInMount()
        {
            const string searchPattern = "*";
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
            };

            return Directory.GetFileSystemEntries(temp.Path, searchPattern, enumerationOptions);
        }
    }

    private static bool IsResourceLog(FakeLogRecord record)
    {
        return (record.Category ?? string.Empty).StartsWith("Aspire.Hosting.Tests.Resources", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A custom exception used to indicate that a test should be retried.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    private sealed class RetryableTestException(string message) : Exception(message)
    {
    }
}
