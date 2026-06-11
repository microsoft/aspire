// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Aspire.StackExchange.Redis.Tests;

public class ConformanceTests : ConformanceTests<IConnectionMultiplexer, StackExchangeRedisSettings>, IClassFixture<RedisContainerFixture>
{
    private readonly RedisContainerFixture _containerFixture;

    protected string ConnectionString => _containerFixture.GetConnectionString();

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override bool SupportsKeyedRegistrations => true;

    protected override string[] RequiredLogCategories => ["StackExchange.Redis.ConnectionMultiplexer"];

    protected override string? ConfigurationSectionName => "Aspire:StackExchange:Redis";

    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/e4cb523a4a3592e1a1adf30f3596025bfd8978e3/src/OpenTelemetry.Instrumentation.StackExchangeRedis/StackExchangeRedisConnectionInstrumentation.cs#L34
    protected override string ActivitySourceName => "OpenTelemetry.Instrumentation.StackExchangeRedis";

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "StackExchange": {
              "Redis": {
                "ConnectionString": "YOUR_ENDPOINT",
                "DisableHealthChecks": false,
                "DisableTracing": true,
                "ConfigurationOptions": {
                  "CheckCertificateRevocation": true,
                  "ConnectTimeout": 5,
                  "HeartbeatInterval": "00:00:02",
                  "Ssl" : true,
                  "SslProtocols" : "Tls11"
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "StackExchange": { "Redis":{ "ConfigurationOptions": "YOUR_OPTION"}}}}""", "Value is \"string\" but should be \"object\""),
            ("""{"Aspire": { "StackExchange": { "Redis":{ "ConfigurationOptions": { "Proxy": "Fast"}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "StackExchange": { "Redis":{ "ConfigurationOptions": { "SslProtocols": "Fast"}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "StackExchange": { "Redis":{ "ConfigurationOptions": { "HeartbeatInterval": "3S"}}}}}""", "The string value is not a match for the indicated regular expression")
        };

    public ConformanceTests(RedisContainerFixture containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        string connectionString = RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker)
                                    ? _containerFixture.GetConnectionString()
                                    : "localhost";
        configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:StackExchange:Redis", key, "ConnectionString"), connectionString)
        ]);
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<StackExchangeRedisSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddRedisClient("redis", configure);
        }
        else
        {
            builder.AddKeyedRedisClient(key, configure);
        }
    }

    protected override void SetHealthCheck(StackExchangeRedisSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(StackExchangeRedisSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(StackExchangeRedisSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void TriggerActivity(IConnectionMultiplexer service)
    {
        var database = service.GetDatabase();

        string id = Guid.NewGuid().ToString();
        database.StringSet(id, "hello");
        database.KeyDelete(id);
    }
}
