// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Aspire.Npgsql.Tests;
using Aspire.TestUtilities;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL.Tests;

public class ConformanceTests : ConformanceTests<TestDbContext, AzureNpgsqlEntityFrameworkCorePostgreSQLSettings>, IClassFixture<PostgreSQLContainerFixture>
{
    // in the future it can become a static property that reads the value from Env Var
    private readonly PostgreSQLContainerFixture? _containerFixture;
    protected string ConnectionString { get; private set; }
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    // https://github.com/npgsql/npgsql/blob/ef9db1ffe9e432c1562d855b46dfac3514726b1b/src/Npgsql.OpenTelemetry/TracerProviderBuilderExtensions.cs#L18
    protected override string ActivitySourceName => "Npgsql";

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

    // we don't want to have both EF and Npgsql loggers to be enabled
    protected override string[] NotAcceptableLogCategories => new string[]
    {
        "Npgsql.Connection",
        "Npgsql.Command",
        "Npgsql.Transaction",
        "Npgsql.Copy",
        "Npgsql.Replication",
        "Npgsql.Exception"
    };

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Npgsql": {
              "EntityFrameworkCore": {
                "PostgreSQL": {
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
            ("""{"Aspire": { "Npgsql": { "EntityFrameworkCore":{ "PostgreSQL": { "DisableRetry": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Npgsql": { "EntityFrameworkCore":{ "PostgreSQL": { "DisableHealthChecks": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Npgsql": { "EntityFrameworkCore":{ "PostgreSQL": { "DisableTracing": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Npgsql": { "EntityFrameworkCore":{ "PostgreSQL": { "DisableMetrics": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    public ConformanceTests(PostgreSQLContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : "Server=localhost;User ID=root;Password=password;Database=test_aspire_mysql";
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:ConnectionString", ConnectionString)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureNpgsqlEntityFrameworkCorePostgreSQLSettings>? configure = null, string? key = null)
        => builder.AddAzureNpgsqlDbContext<TestDbContext>(key ?? "postgres", configure);

    protected override void SetHealthCheck(AzureNpgsqlEntityFrameworkCorePostgreSQLSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(AzureNpgsqlEntityFrameworkCorePostgreSQLSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(AzureNpgsqlEntityFrameworkCorePostgreSQLSettings options, bool enabled)
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
                RunWithConnectionString(connectionStringToUse, obj =>
                    obj.ActivitySourceTest(key: null)), ConnectionString, Output);

    private static void RunWithConnectionString(string connectionString, Action<ConformanceTests> test)
        => test(new ConformanceTests(null) { ConnectionString = connectionString });
}
