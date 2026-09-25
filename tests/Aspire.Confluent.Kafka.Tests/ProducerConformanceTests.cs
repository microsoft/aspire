// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

public class ProducerConformanceTests : ConformanceTests<IProducer<string, string>, KafkaProducerSettings>
{
    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => throw new NotImplementedException();

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Aspire.Confluent.Kafka")];

    protected override string? ConfigurationSectionName => "Aspire:Confluent:Kafka:Producer";

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
    {
        configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[1]
        {
            new KeyValuePair<string, string?>(CreateConfigKey("Aspire:Confluent:Kafka:Producer", key, "ConnectionString"),
                           "localhost:9092")
        });
    }

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<KafkaProducerSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddKafkaProducer<string, string>("messaging", configure);
        }
        else
        {
            builder.AddKeyedKafkaProducer<string, string>(key, configure);
        }
    }

    protected override void SetHealthCheck(KafkaProducerSettings options, bool enabled) => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(KafkaProducerSettings options, bool enabled) => options.DisableMetrics = !enabled;

    protected override void SetTracing(KafkaProducerSettings options, bool enabled)
    {
        throw new NotImplementedException();
    }

    protected override void TriggerActivity(IProducer<string, string> service)
    {
        service.Produce("test", new Message<string, string> { Key = "test", Value = "test" });
        service.Flush(TimeSpan.FromMilliseconds(1000));
    }

    protected override bool SupportsKeyedRegistrations => true;

    protected override string ValidJsonConfig => """
                {
                    "Aspire": {
                        "Confluent": {
                            "Kafka": {
                                "Producer": {
                                    "ConnectionString": "localhost:9092",
                                    "DisableHealthChecks": false,
                                    "DisableMetrics": false
                                }
                            }
                        }
                    }
                }
                """;
    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Confluent":{ "Kafka": { "Producer": { "DisableMetrics": 0}}}}}""", "Value is \"integer\" but should be \"boolean\""),
            ("""{"Aspire": { "Confluent":{ "Kafka": { "Producer": { "DisableHealthChecks": 0}}}}}""", "Value is \"integer\" but should be \"boolean\"")
        };

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging1", "localhost:9091"),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging2", "localhost:9092"),
            new KeyValuePair<string, string?>("ConnectionStrings:messaging3", "localhost:9093"),
        ]);

        builder.AddKafkaProducer<string, string>("messaging1");
        builder.AddKeyedKafkaProducer<string, string>("messaging2");
        builder.AddKeyedKafkaProducer<string, string>("messaging3");

        using var host = builder.Build();

        var client1 = host.Services.GetRequiredService<IProducer<string, string>>();
        var client2 = host.Services.GetRequiredKeyedService<IProducer<string, string>>("messaging2");
        var client3 = host.Services.GetRequiredKeyedService<IProducer<string, string>>("messaging3");

        Assert.NotSame(client1, client2);
        Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);
    }
}
