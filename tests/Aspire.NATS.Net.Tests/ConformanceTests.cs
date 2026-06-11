// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Components.ConformanceTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Xunit;

namespace Aspire.NATS.Net.Tests;

public class ConformanceTests : ConformanceTests<INatsConnection, NatsClientSettings>, IClassFixture<NatsContainerFixture>
{
    private readonly NatsContainerFixture _containerFixture;
    private readonly string _connectionString;
    public ConformanceTests(NatsContainerFixture containerFixture, ITestOutputHelper? output = null) : base(output)
    {
        _containerFixture = containerFixture;
        _connectionString = RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker)
            ? _containerFixture.GetConnectionString()
            : "nats://user:password@apire-host:4222";
    }

    protected override bool SupportsKeyedRegistrations => true;
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;
    protected override string ActivitySourceName => "NATS.Net";
    protected override string[] RequiredLogCategories => [
        "NATS.Client.Core.Commands.CommandWriter",
        "NATS.Client.Core.Internal.SubscriptionManager",
        "NATS.Client.Core.Internal.InboxSubBuilder",
        "NATS.Client.Core.NatsSubBase",
        "NATS.Client.Core.NatsConnection",
    ];

    protected override string? ConfigurationSectionName => "Aspire:NATS:Net";

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<NatsClientSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddNatsClient("nats", configure);
        }
        else
        {
            builder.AddKeyedNatsClient(key, configure);
        }
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Nats:Net", key, "ConnectionString"), _connectionString)
        });

    protected override bool CanCreateClientWithoutConnectingToServer => false;

    protected override bool CanConnectToServer => RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker);

    protected override void TriggerActivity(INatsConnection service)
    {
    }

    protected override void SetHealthCheck(NatsClientSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(NatsClientSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(NatsClientSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override string ValidJsonConfig => """
                                                 {
                                                   "Aspire": {
                                                     "Nats": {
                                                       "Client": {
                                                         "ConnectionString": "YOUR_CONNECTION_STRING",
                                                         "DisableHealthChecks": true,
                                                         "DisableTracing": false,
                                                         "DisableMetrics": false
                                                       }
                                                     }
                                                   }
                                                 }
                                                 """;
}
