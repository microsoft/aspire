// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.Data.SqlClient.Tests;

public class ConformanceTests : ConformanceTests<SqlConnection, MicrosoftDataSqlClientSettings>, IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture? _containerFixture;
    private string ConnectionString { get; set; }
    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Scoped;

    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/031ed48714e16ba4a5b099b6e14647994a0b9c1b/src/OpenTelemetry.Instrumentation.SqlClient/Implementation/SqlActivitySourceHelper.cs#L31
    protected override string ActivitySourceName => "OpenTelemetry.Instrumentation.SqlClient";

    // TODO
    protected override string[] RequiredLogCategories => Array.Empty<string>();

    protected override bool SupportsKeyedRegistrations => true;

    protected override string? ConfigurationSectionName => "Aspire:Microsoft:Data:SqlClient";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "SqlServer": {
              "SqlClient": {
                "ConnectionString": "YOUR_CONNECTION_STRING",
                "DisableHealthChecks": false,
                "DisableTracing": false
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Microsoft": { "Data" : { "SqlClient":{ "DisableTracing": 0}}}}}""", "Value is \"integer\" but should be \"boolean\""),
            ("""{"Aspire": { "Microsoft": { "Data" : { "SqlClient":{ "ConnectionString": "Con", "DisableHealthChecks": "true"}}}}}""", "Value is \"string\" but should be \"boolean\"")
        };

    public ConformanceTests(SqlServerContainerFixture? containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        ConnectionString = (_containerFixture is not null && RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
                                        ? _containerFixture.GetConnectionString()
                                        : "Server=localhost;User ID=root;Password=password;Database=test_aspire_mysql";
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Microsoft:Data:SqlClient", key, "ConnectionString"),
                ConnectionString)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<MicrosoftDataSqlClientSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddSqlServerClient("sqlconnection", configure);
        }
        else
        {
            builder.AddKeyedSqlServerClient(key, configure);
        }
    }

    protected override void SetHealthCheck(MicrosoftDataSqlClientSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(MicrosoftDataSqlClientSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(MicrosoftDataSqlClientSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void TriggerActivity(SqlConnection service)
    {
        service.Open();
        using var command = service.CreateCommand();
        command.CommandText = "SELECT 1;";
        command.ExecuteScalar();
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

