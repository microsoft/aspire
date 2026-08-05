// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Components.ConformanceTests;
using HealthChecks.Azure.Messaging.EventHubs;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Azure.Messaging.EventHubs.Tests;

public abstract class ConformanceTestsBase<TService, TOptions> : ConformanceTests<TService, TOptions>
    where TService : class
    where TOptions : class, new()
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => $"Azure.Messaging.EventHubs.{typeof(TService).Name}";

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Azure.Messaging.EventHubs", AllowPrefixMatch: true)];

    protected override string ValidJsonConfig => $$"""
        {
          "Aspire": {
            "Azure": {
              "Messaging": {
                "EventHubs": {
                  "{{typeof(TService).Name}}": {
                      "DisableHealthChecks": false,
                      "BlobClientServiceKey": "blobs",
                      "ClientOptions": {
                        "ConnectionIdleTimeout": "00:01",
                        "EnableCrossEntityTransactions": true,
                        "RetryOptions": {
                          "Mode": "Fixed",
                          "MaxDelay": "00:03"
                        },
                        "TransportType": "AmqpWebSockets"
                     }
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage =>
        [
             ($$"""{"Aspire": { "Azure": { "Messaging" :{ "EventHubs": { "{{typeof(TService).Name}}": { "DisableHealthChecks": "true" } } } } } }""", "Value is \"string\" but should be \"boolean\""),
        ];

    public ConformanceTestsBase(ITestOutputHelper? output = null) : base(output)
    {
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    => configuration.AddInMemoryCollection(
    [
        new($"Aspire:Azure:Messaging:EventHubs:{typeof(TService).Name}:ConnectionString", AspireEventHubsExtensionsTests.EhConnectionString)
    ]);

    protected static RemoteInvokeOptions EnableTracingForAzureSdk()
     => new()
     {
         RuntimeConfigurationOptions = { { "Azure.Experimental.EnableActivitySource", true } }
     };

    [Fact]
    public void HealthChecksClientsAreReused()
    {
        // DisableRetries so the test doesn't take so long retrying when the server isn't available.
        using IHost host = CreateHostWithComponent(configureComponent: DisableRetries);

        HealthCheckServiceOptions healthCheckServiceOptions = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        var registration = healthCheckServiceOptions.Registrations.First();

        var healthCheck1 = registration.Factory(host.Services) as AzureEventHubHealthCheck;
        var healthCheck2 = registration.Factory(host.Services) as AzureEventHubHealthCheck;

        Assert.NotNull(healthCheck1);
        Assert.NotNull(healthCheck2);

        var clientAccessor = typeof(AzureEventHubHealthCheck).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(clientAccessor);

        var client1 = clientAccessor?.GetValue(healthCheck1);
        var client2 = clientAccessor?.GetValue(healthCheck2);

        Assert.Same(client1, client2);
    }
}
