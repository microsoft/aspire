// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Xunit;

namespace Aspire.MongoDB.Driver.Tests;

public class ConformanceTests : ConformanceTests<IMongoClient, MongoDBSettings>, IClassFixture<MongoDbContainerFixture>
{
    private readonly MongoDbContainerFixture _containerFixture;

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "MongoDB.Driver.Core.Extensions.DiagnosticSources";

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string? ConfigurationSectionName => "Aspire:MongoDB:Driver";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "MongoDB": {
              "Driver": {
                "ConnectionString": "YOUR_CONNECTION_STRING",
                "DisableHealthChecks": false,
                "HealthCheckTimeout": 100,
                "DisableTracing": false
              }
            }
          }
        }
        """;

    public ConformanceTests(MongoDbContainerFixture containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
    }

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
    {
        ("""{"Aspire": { "MongoDB":{ "Driver": { "DisableHealthChecks": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
        ("""{"Aspire": { "MongoDB":{ "Driver": { "HealthCheckTimeout": "10000"}}}}""", "Value is \"string\" but should be \"integer\""),
        ("""{"Aspire": { "MongoDB":{ "Driver": { "DisableTracing": "true"}}}}""", "Value is \"string\" but should be \"boolean\""),
    };

    protected override string[] RequiredLogCategories => [
        "MongoDB.SDAM",
        "MongoDB.ServerSelection",
        "MongoDB.Connection",
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        var connectionString = GetConnectionString();

        configuration.AddInMemoryCollection(
            [
            new KeyValuePair<string, string?>(
                CreateConfigKey("Aspire:MongoDB:Driver", key, "ConnectionString"),
                connectionString)
            ]);
    }

    private string GetConnectionString()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            var builder = new UriBuilder(_containerFixture.GetConnectionString());
            builder.Path = "test_db";
            return builder.ToString();
        }
        return "mongodb://root:password@localhost:27017/test_db?authSource=admin&authMechanism=SCRAM-SHA-256";
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MongoDBSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddMongoDBClient("mongodb", configure);
        }
        else
        {
            builder.AddKeyedMongoDBClient(key, configure);
        }
    }

    protected override void SetHealthCheck(MongoDBSettings options, bool enabled)
    {
        options.DisableHealthChecks = !enabled;
        options.HealthCheckTimeout = 10;
    }

    protected override void SetMetrics(MongoDBSettings options, bool enabled) => throw new NotImplementedException();

    protected override void SetTracing(MongoDBSettings options, bool enabled)
    {
        options.DisableTracing = !enabled;
    }

    protected override void TriggerActivity(IMongoClient service)
    {
        using var source = new CancellationTokenSource(10);

        service.ListDatabases(source.Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public void ClientAndDatabaseInstancesShouldBeResolved(string? key)
    {
        using IHost host = CreateHostWithComponent(key: key);

        IMongoClient? mongoClient = Resolve<IMongoClient>();
        IMongoDatabase? mongoDatabase = Resolve<IMongoDatabase>();

        Assert.NotNull(mongoClient);
        Assert.NotNull(mongoDatabase);

        T? Resolve<T>() => key is null ? host.Services.GetService<T>() : host.Services.GetKeyedService<T>(key);
    }
}
