// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Aspire.Hosting.MySql;
using Aspire.MySqlConnector.Tests;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Pomelo.EntityFrameworkCore.MySql.Tests;

public class ConformanceTests : ConformanceTests<TestDbContext, PomeloEntityFrameworkCoreMySqlSettings>, IClassFixture<MySqlContainerFixture>
{
    private readonly MySqlContainerFixture? _containerFixture;
    protected string ConnectionString { get; private set; }
    protected readonly string ServerVersion = $"{MySqlContainerImageTags.Tag}-mysql";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    // https://github.com/mysql-net/MySqlConnector/blob/6a63fa49795b54318085938e4a09cda0bc0ab2cd/src/MySqlConnector/Utilities/ActivitySourceHelper.cs#L61
    protected override string ActivitySourceName => "MySqlConnector";

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
        "Microsoft.EntityFrameworkCore.Migrations",
        "MySqlConnector.ConnectionPool",
        "MySqlConnector.MySqlBulkCopy",
        "MySqlConnector.MySqlCommand",
        "MySqlConnector.MySqlConnection",
        "MySqlConnector.MySqlDataSource",
    };

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Pomelo": {
              "EntityFrameworkCore": {
                "MySql": {
                  "ConnectionString": "YOUR_CONNECTION_STRING",
                  "DisableHealthChecks": true,
                  "DisableTracing": false,
                  "DisableMetrics": false
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Pomelo": { "EntityFrameworkCore":{ "MySql": { "DisableRetry": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Pomelo": { "EntityFrameworkCore":{ "MySql": { "DisableHealthChecks": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Pomelo": { "EntityFrameworkCore":{ "MySql": { "DisableTracing": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Pomelo": { "EntityFrameworkCore":{ "MySql": { "DisableMetrics": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    public ConformanceTests(MySqlContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : "Server=localhost;User ID=root;Password=password;Database=test_aspire_mysql";
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[2]
        {
            new("Aspire:Pomelo:EntityFrameworkCore:MySql:ConnectionString", ConnectionString),
            new("Aspire:Pomelo:EntityFrameworkCore:MySql:ServerVersion", ServerVersion)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<PomeloEntityFrameworkCoreMySqlSettings>? configure = null, string? key = null)
        => builder.AddMySqlDbContext<TestDbContext>(key ?? "mysql", configure);

    protected override void SetHealthCheck(PomeloEntityFrameworkCoreMySqlSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(PomeloEntityFrameworkCoreMySqlSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(PomeloEntityFrameworkCoreMySqlSettings options, bool enabled)
        => options.DisableMetrics = !enabled;

    protected override void TriggerActivity(TestDbContext service)
    {
        if (service.Database.CanConnect())
        {
            service.Database.EnsureCreated();
        }
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
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, obj => obj.ActivitySourceTest(key: null)),
            ConnectionString, Output);

    private static void RunWithConnectionString(string connectionString, Action<ConformanceTests> test)
        => test(new ConformanceTests(null) { ConnectionString = connectionString });
}
