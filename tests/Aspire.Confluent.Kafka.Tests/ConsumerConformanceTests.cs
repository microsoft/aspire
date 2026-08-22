// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;
public class ConsumerConformanceTests : ConformanceTests<IConsumer<string, string>, KafkaConsumerSettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => throw new NotImplementedException();

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Aspire.Confluent.Kafka")];

    protected override string? ConfigurationSectionName => "Aspire:Confluent:Kafka:Consumer";

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "ConnectionString"),
                           "localhost:9092"),
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"),
                           "test")
        });
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<KafkaConsumerSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddKafkaConsumer<string, string>("messaging", configure);
        }
        else
        {
            builder.AddKeyedKafkaConsumer<string, string>(key, configure);
        }
    }

    protected override void SetHealthCheck(KafkaConsumerSettings options, bool enabled) => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(KafkaConsumerSettings options, bool enabled) => options.DisableMetrics = !enabled;

    protected override void SetTracing(KafkaConsumerSettings options, bool enabled)
    {
        throw new NotImplementedException();
    }

    protected override void TriggerActivity(IConsumer<string, string> service)
    {
        service.Subscribe("test");
    }

    protected override bool SupportsKeyedRegistrations => true;

    protected override string ValidJsonConfig => """
                {
                    "Aspire": {
                        "Confluent": {
                            "Kafka": {
                                "Consumer": {
                                    "ConnectionString": "localhost:9092",
                                    "DisableHealthChecks": false,
                                    "DisableMetrics": false,
                                    "Config": {
                                        "GroupId": "test"
                                    }
                                }
                            }
                        }
                    }
                }
                """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Confluent":{ "Kafka": { "Consumer": { "DisableMetrics": 0}}}}}""", "Value is \"integer\" but should be \"boolean\""),
            ("""{"Aspire": { "Confluent":{ "Kafka": { "Consumer": { "DisableHealthChecks": 0}}}}}""", "Value is \"integer\" but should be \"boolean\"")
        };

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging1", "localhost:9091"),
            new KeyValuePair<string, string?>("Aspire:Confluent:Kafka:Consumer:Config:GroupId", "messaging1"),

            new KeyValuePair<string, string?>("ConnectionStrings:messaging2", "localhost:9092"),
            new KeyValuePair<string, string?>("Aspire:Confluent:Kafka:Consumer:messaging2:Config:GroupId", "messaging2"),

            new KeyValuePair<string, string?>("ConnectionStrings:messaging3", "localhost:9093"),
            new KeyValuePair<string, string?>("Aspire:Confluent:Kafka:Consumer:messaging3:Config:GroupId", "messaging3"),
        ]);

        builder.AddKafkaConsumer<string, string>("messaging1");
        builder.AddKeyedKafkaConsumer<string, string>("messaging2");
        builder.AddKeyedKafkaConsumer<string, string>("messaging3");

        using var host = builder.Build();

        var client1 = host.Services.GetRequiredService<IConsumer<string, string>>();
        var client2 = host.Services.GetRequiredKeyedService<IConsumer<string, string>>("messaging2");
        var client3 = host.Services.GetRequiredKeyedService<IConsumer<string, string>>("messaging3");

        Assert.NotSame(client1, client2);
        Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);
    }
}
