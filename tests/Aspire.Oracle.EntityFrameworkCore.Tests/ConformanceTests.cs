// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Oracle.ManagedDataAccess.OpenTelemetry;
using Xunit;

namespace Aspire.Oracle.EntityFrameworkCore.Tests;

[Collection("Oracle Database collection")]
public class ConformanceTests : ConformanceTests<TestDbContext, OracleEntityFrameworkCoreSettings>
{
    private readonly OracleContainerFixture? _containerFixture;
    private readonly ITestOutputHelper? _testOutputHelper;

    protected string ConnectionString { get; private set; }

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Oracle.ManagedDataAccess.Core";

    protected override string[] RequiredLogCategories => new string[]
    {
        "Microsoft.EntityFrameworkCore.Infrastructure",
        "Microsoft.EntityFrameworkCore.ChangeTracking",
        "Microsoft.EntityFrameworkCore.Infrastructure",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "Microsoft.EntityFrameworkCore.Query",
        "Microsoft.EntityFrameworkCore.Database.Transaction",
        "Microsoft.EntityFrameworkCore.Database.Connection",
        "Microsoft.EntityFrameworkCore.Model",
        "Microsoft.EntityFrameworkCore.Model.Validation",
        "Microsoft.EntityFrameworkCore.Update",
        "Microsoft.EntityFrameworkCore.Migrations"
    };

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Oracle": {
              "EntityFrameworkCore": {
                "ConnectionString": "YOUR_CONNECTION_STRING",
                "DisableHealthChecks": true,
                "DisableTracing": false
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Oracle": { "EntityFrameworkCore":{ "DisableRetry": "5"}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Oracle": { "EntityFrameworkCore":{ "DisableHealthChecks": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Oracle": { "EntityFrameworkCore":{ "DisableTracing": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new ("Aspire:Oracle:EntityFrameworkCore:ConnectionString", ConnectionString)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<OracleEntityFrameworkCoreSettings>? configure = null, string? key = null)
        => builder.AddOracleDatabaseDbContext<TestDbContext>("orclconnection", configure);

    protected override void SetHealthCheck(OracleEntityFrameworkCoreSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(OracleEntityFrameworkCoreSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(OracleEntityFrameworkCoreSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void TriggerActivity(TestDbContext service)
    {
        if (service.Database.CanConnect())
        {
            service.Database.EnsureCreated();
        }
        else
        {
            Assert.Fail($"Cannot connect to database: {ConnectionString}");
        }
    }

    public ConformanceTests(OracleContainerFixture? containerFixture, ITestOutputHelper? testOutputHelper = null) : base(testOutputHelper)
    {
        _containerFixture = containerFixture;
        _testOutputHelper = testOutputHelper;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : "Server=localhost;User ID=oracle;Password=oracle;Database=FREEPDB1";
    }

    [Fact]
    public void DbContextPoolingRegistersIDbContextPool()
    {
        using IHost host = CreateHostWithComponent();

#pragma warning disable EF1001 // Internal EF Core API usage.
        IDbContextPool<TestDbContext>? pool = host.Services.GetService<IDbContextPool<TestDbContext>>();
#pragma warning restore EF1001

        Assert.NotNull(pool);
    }

    [Fact]
    public void DbContextCanBeAlwaysResolved()
    {
        using IHost host = CreateHostWithComponent();

        TestDbContext? dbContext = host.Services.GetService<TestDbContext>();

        Assert.NotNull(dbContext);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource()
    {
        RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, obj => obj.ActivitySourceTest(key: null)),
            ConnectionString, Output);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingHasSemanticConventions()
    {
        RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, obj => obj.ActivitySemanticsTest()),
            ConnectionString, Output);
    }

    private void ActivitySemanticsTest()
    {
        HostApplicationBuilder builder = CreateHostBuilder();
        RegisterComponent(builder, options => SetTracing(options, true));

        List<Activity> exportedActivities = new();
        builder.Services.AddOpenTelemetry().WithTracing(builder =>
        {
            builder.AddInMemoryExporter(exportedActivities);
            builder.AddOracleDataProviderInstrumentation(o => o.EnableConnectionLevelAttributes = true);
        });

        using (IHost host = builder.Build())
        {
            // We start the host to make it build TracerProvider.
            // If we don't, nothing gets reported!
            host.Start();

            var service = host.Services.GetRequiredService<TestDbContext>();

            Assert.Empty(exportedActivities);

            try
            {
                TriggerActivity(service);
            }
            catch (Exception) when (!CanConnectToServer)
            {
            }

            Assert.NotEmpty(exportedActivities);
            //Test runner doesn't have the server port set
            Assert.Contains(exportedActivities, activity => activity.Tags.Any(x => x.Key == "server.address"));
            Assert.Contains(exportedActivities, activity => activity.Tags.Any(x => x.Key == "db.system"));
        }
    }

    private static void RunWithConnectionString(string connectionString, Action<ConformanceTests> test)
        => test(new ConformanceTests(null, null) { ConnectionString = connectionString });
}
