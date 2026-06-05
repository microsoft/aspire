// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Aspire.MongoDB.Driver.Tests;
using Aspire.TestUtilities;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.MongoDB.EntityFrameworkCore.Tests;

public class ConformanceTests : ConformanceTests<TestDbContext, MongoDBEntityFrameworkCoreSettings>, IClassFixture<MongoDbContainerFixture>
{
    // in the future it can become a static property that reads the value from Env Var
    private readonly MongoDbContainerFixture? _containerFixture;
    protected string ConnectionString { get; private set; }
    protected string DatabaseName { get; private set; }
    protected override ServiceLifetime ServiceLifetime { get; }
    protected override string ActivitySourceName => "MongoDB.Driver.Core.Extensions.DiagnosticSources";
    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string[] RequiredLogCategories => new string[]
    {
        "Microsoft.EntityFrameworkCore.Infrastructure",
        "Microsoft.EntityFrameworkCore.ChangeTracking",
        "Microsoft.EntityFrameworkCore.Infrastructure",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "Microsoft.EntityFrameworkCore.Query",
        "Microsoft.EntityFrameworkCore.Database.Transaction",
        "Microsoft.EntityFrameworkCore.Model",
        "Microsoft.EntityFrameworkCore.Model.Validation",
        "Microsoft.EntityFrameworkCore.Update"
    };

    // we don't want to have both EF and MongoDB loggers to be enabled
    protected override string[] NotAcceptableLogCategories => new string[]
    {
        "MongoDB.SDAM",
        "MongoDB.ServerSelection",
        "MongoDB.Connection"
    };

    protected override string ValidJsonConfig => """
         {
           "Aspire": {
             "MongoDB": {
               "EntityFrameworkCore": {
                 "ConnectionString": "YOUR_CONNECTION_STRING",
                 "DatabaseName": "YOUR_DATABASE_NAME",
                 "DisableHealthChecks": false,
                 "HealthCheckTimeout": 100,
                 "DisableTracing": false
               }
             }
           }
         }
         """;
    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
    {
        ("""{"Aspire": { "MongoDB":{ "EntityFrameworkCore": { "DisableHealthChecks": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
        ("""{"Aspire": { "MongoDB":{ "EntityFrameworkCore": { "HealthCheckTimeout": "10000"}}}}""", "Value is \"string\" but should be \"integer\""),
        ("""{"Aspire": { "MongoDB":{ "EntityFrameworkCore": { "DisableTracing": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
    };

    public ConformanceTests(MongoDbContainerFixture? containerFixture)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
            ? _containerFixture.GetConnectionString()
            : "mongodb://localhost:27017/test_aspire_mongodb";
        DatabaseName = "test_db";
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MongoDBEntityFrameworkCoreSettings>? configure = null, string? key = null)
        => builder.AddMongoDbContext<TestDbContext>(key ?? "mongodb","test_db" ,configure);

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new("Aspire:MongoDB:EntityFrameworkCore:ConnectionString", ConnectionString),
            new("Aspire:MongoDB:EntityFrameworkCore:DatabaseName", DatabaseName),
        });

    protected override void TriggerActivity(TestDbContext service)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (service.Database.CanConnect())
            {
                service.Database.EnsureCreated();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when connection times out
        }
    }

    protected override void SetHealthCheck(MongoDBEntityFrameworkCoreSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(MongoDBEntityFrameworkCoreSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(MongoDBEntityFrameworkCoreSettings options, bool enabled) => throw new NotImplementedException();

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "EF1001:Internal EF Core API usage.", Justification = "Required to verify pooling without touching DB")]
    public void DbContextPoolingRegistersIDbContextPool()
    {
        using IHost host = CreateHostWithComponent();

        IDbContextPool<TestDbContext>? pool = host.Services.GetService<IDbContextPool<TestDbContext>>();

        Assert.NotNull(pool);
    }

    [Fact]
    public void DbContextCanBeAlwaysResolved()
    {
        using IHost host = CreateHostWithComponent();

        TestDbContext? dbContext = host.Services.GetService<TestDbContext>();

        Assert.NotNull(dbContext);
    }
}
