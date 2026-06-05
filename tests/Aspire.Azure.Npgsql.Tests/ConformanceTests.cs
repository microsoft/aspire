// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Aspire.Components.ConformanceTests;
using Aspire.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Aspire.Azure.Npgsql.Tests;

public class ConformanceTests : ConformanceTests<NpgsqlDataSource, AzureNpgsqlSettings>, IClassFixture<PostgreSQLContainerFixture>
{
    private readonly PostgreSQLContainerFixture? _containerFixture;
    protected string ConnectionString { get; private set; }
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    // https://github.com/npgsql/npgsql/blob/ef9db1ffe9e432c1562d855b46dfac3514726b1b/src/Npgsql.OpenTelemetry/TracerProviderBuilderExtensions.cs#L18
    protected override string ActivitySourceName => "Npgsql";

    protected override string[] RequiredLogCategories => new string[]
    {
        "Npgsql.Connection",
        "Npgsql.Command",
        "Npgsql.Transaction",
        "Npgsql.Copy",
        "Npgsql.Replication",
        "Npgsql.Exception"
    };

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override string? ConfigurationSectionName => "Aspire:Npgsql";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Npgsql": {
              "ConnectionString": "YOUR_CONNECTION_STRING",
              "DisableHealthChecks": true,
              "DisableTracing": false,
              "DisableMetrics": false
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => [];

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
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Npgsql", key, "ConnectionString"), ConnectionString)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureNpgsqlSettings>? configure = null, string? key = null)
    {
        void Configure(AzureNpgsqlSettings settings)
        {
            configure?.Invoke(settings);
            settings.Credential = new FakeTokenCredential();
        };

        if (key is null)
        {
            builder.AddAzureNpgsqlDataSource("npgsql", Configure);
        }
        else
        {
            builder.AddKeyedAzureNpgsqlDataSource(key, Configure);
        }
    }

    protected override void SetHealthCheck(AzureNpgsqlSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(AzureNpgsqlSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(AzureNpgsqlSettings options, bool enabled)
        => options.DisableMetrics = !enabled;

    protected override void TriggerActivity(NpgsqlDataSource service)
    {
        using NpgsqlConnection connection = service.CreateConnection();
        connection.Open();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "Select 1;";
        command.ExecuteScalar();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public void BothDataSourceAndConnectionCanBeResolved(string? key)
    {
        using IHost host = CreateHostWithComponent(key: key);

        NpgsqlDataSource? npgsqlDataSource = Resolve<NpgsqlDataSource>();
        DbDataSource? dbDataSource = Resolve<DbDataSource>();
        NpgsqlConnection? npgsqlConnection = Resolve<NpgsqlConnection>();
        DbConnection? dbConnection = Resolve<DbConnection>();

        Assert.NotNull(npgsqlDataSource);
        Assert.Same(npgsqlDataSource, dbDataSource);

        Assert.NotNull(npgsqlConnection);
        Assert.NotNull(dbConnection);

        Assert.Equal(dbConnection.ConnectionString, npgsqlConnection.ConnectionString);
        Assert.Equal(npgsqlDataSource.ConnectionString, npgsqlConnection.ConnectionString);

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
