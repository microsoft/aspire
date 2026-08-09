// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.EntityFrameworkCore.Cosmos.Tests;

public class ConformanceTests : ConformanceTests<TestDbContext, EntityFrameworkCoreCosmosSettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Cosmos.Operation";

    protected override RequiredLogCategory[] RequiredLogCategories =>
    [
        new("Microsoft.EntityFrameworkCore.ChangeTracking"),
        new("Microsoft.EntityFrameworkCore.Database.Command"),
        new("Microsoft.EntityFrameworkCore.Infrastructure"),
        new("Microsoft.EntityFrameworkCore.Query"),
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new KeyValuePair<string, string?>("Aspire:Microsoft:EntityFrameworkCore:Cosmos:ConnectionString",
                AspireAzureEfCoreCosmosDBExtensionsTests.ConnectionString),
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<EntityFrameworkCoreCosmosSettings>? configure = null, string? key = null)
        => builder.AddCosmosDbContext<TestDbContext>(key ?? "cosmosdb", "TestDatabase", configure);

    protected override void SetHealthCheck(EntityFrameworkCoreCosmosSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(EntityFrameworkCoreCosmosSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(EntityFrameworkCoreCosmosSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Microsoft": {
              "EntityFrameworkCore": {
                "Cosmos": {
                  "ConnectionString": "YOUR_CONNECTION_STRING",
                  "DisableTracing": false
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
    {
            ("""{"Aspire": { "Microsoft":{ "EntityFrameworkCore": { "Cosmos": { "AccountEndpoint": 3 }}}}}""", "Value is \"integer\" but should be \"string\""),
            ("""{"Aspire": { "Microsoft":{ "EntityFrameworkCore": { "Cosmos": { "AccountEndpoint": "hello" }}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Microsoft":{ "EntityFrameworkCore": { "Cosmos": { "Region": 3 }}}}}""", "Value is \"integer\" but should be \"string\""),
        };

    protected override void TriggerActivity(TestDbContext service)
    {
        if (service.Database.CanConnect())
        {
            service.Database.EnsureCreated();
        }
    }

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

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

    [Fact]
    public void TracingEnablesTheRightActivitySource()
    {
        SkipIfCanNotConnectToServer();

        RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: null), Output);
    }
}
