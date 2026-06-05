// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Xunit;

namespace Aspire.MySqlConnector.Tests;

public class ConformanceTests : ConformanceTests<MySqlDataSource, MySqlConnectorSettings>, IClassFixture<MySqlContainerFixture>
{
    private readonly MySqlContainerFixture? _containerFixture;
    private string ConnectionString { get; set; }
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    // https://github.com/mysql-net/MySqlConnector/blob/d895afc013a5849d33a123a7061442e2cbb9ce76/src/MySqlConnector/Utilities/ActivitySourceHelper.cs#L61
    protected override string ActivitySourceName => "MySqlConnector";

    protected override string[] RequiredLogCategories => [
        "MySqlConnector.ConnectionPool",
        "MySqlConnector.MySqlBulkCopy",
        "MySqlConnector.MySqlCommand",
        "MySqlConnector.MySqlConnection",
        "MySqlConnector.MySqlDataSource",
    ];

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string? ConfigurationSectionName => "Aspire:MySqlConnector";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "MySqlConnector": {
              "ConnectionString": "YOUR_CONNECTION_STRING",
              "DisableHealthChecks": true,
              "DisableTracing": false,
              "DisableMetrics": false
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "MySqlConnector":{ "DisableMetrics": 0}}}""", "Value is \"integer\" but should be \"boolean\""),
            ("""{"Aspire": { "MySqlConnector":{ "ConnectionString": "Con", "DisableHealthChecks": "true"}}}""", "Value is \"string\" but should be \"boolean\"")
        };

    public ConformanceTests(MySqlContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : "Server=localhost;User ID=root;Password=password;Database=test_aspire_mysql";
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:MySqlConnector", key, "ConnectionString"), ConnectionString)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MySqlConnectorSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddMySqlDataSource("mysql", configure);
        }
        else
        {
            builder.AddKeyedMySqlDataSource(key, configure);
        }
    }

    protected override void SetHealthCheck(MySqlConnectorSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(MySqlConnectorSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(MySqlConnectorSettings options, bool enabled)
        => options.DisableMetrics = !enabled;

    protected override void TriggerActivity(MySqlDataSource service)
    {
        using MySqlConnection connection = service.CreateConnection();
        connection.Open();
        using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "Select 1;";
        command.ExecuteScalar();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public void BothDataSourceAndConnectionCanBeResolved(string? key)
    {
        using IHost host = CreateHostWithComponent(key: key);

        MySqlDataSource? mySqlDataSource = Resolve<MySqlDataSource>();
        DbDataSource? dbDataSource = Resolve<DbDataSource>();
        MySqlConnection? mySqlConnection = Resolve<MySqlConnection>();
        DbConnection? dbConnection = Resolve<DbConnection>();

        Assert.NotNull(mySqlDataSource);
        Assert.Same(mySqlDataSource, dbDataSource);

        Assert.NotNull(mySqlConnection);
        Assert.NotNull(dbConnection);

        Assert.Equal(dbConnection.ConnectionString, mySqlConnection.ConnectionString);
        Assert.Equal(mySqlDataSource.ConnectionString, mySqlConnection.ConnectionString);

        T? Resolve<T>() => key is null ? host.Services.GetService<T>() : host.Services.GetKeyedService<T>(key);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, obj => obj.ActivitySourceTest(key: null)),
            ConnectionString, Output);

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteInvokeWithLogging(static connectionStringToUse =>
            RunWithConnectionString(connectionStringToUse, obj => obj.ActivitySourceTest(key: "key")),
            ConnectionString, Output);

    private static void RunWithConnectionString(string connectionString, Action<ConformanceTests> test)
        => test(new ConformanceTests(null) { ConnectionString = connectionString });
}

