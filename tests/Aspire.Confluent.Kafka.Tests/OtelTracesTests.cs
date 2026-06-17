// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

[Collection("Kafka Broker collection")]
public class OtelTracesTests
{
    private readonly KafkaContainerFixture? _containerFixture;
    private readonly ITestOutputHelper _outputHelper;

    public OtelTracesTests(KafkaContainerFixture? kafkaContainerFixture, ITestOutputHelper outputHelper)
    {
        _containerFixture = kafkaContainerFixture;
        _outputHelper = outputHelper;
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task EnsureTracesAreProducedAsync(bool useKeyed)
    {
        List<Activity> activities = new();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture?.Container?.GetBootstrapAddress()),
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaProducer<string, string>("messaging");
            builder.AddKeyedKafkaConsumer<string, string>("messaging", configureSettings: settings =>
            {
                settings.Config.GroupId = "unused";
                settings.Config.EnablePartitionEof = true;
                settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
            });
        }
        else
        {
            builder.AddKafkaProducer<string, string>("messaging");
            builder.AddKafkaConsumer<string, string>("messaging", configureSettings: settings =>
            {
                settings.Config.GroupId = "unused";
                settings.Config.EnablePartitionEof = true;
                settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
            });
        }

        builder.Services.AddOpenTelemetry().WithTracing(traceProviderBuilder => traceProviderBuilder.AddInMemoryExporter(activities));

        using var host = builder.Build();
        await host.StartAsync();

        string topic = $"otel-topic-{Guid.NewGuid()}";
        using (var producer = useKeyed
            ? host.Services.GetRequiredKeyedService<IProducer<string, string>>(key)
            : host.Services.GetRequiredService<IProducer<string, string>>())
        {
            for (int i = 0; i < 5; i++)
            {
                producer.Produce(topic, new Message<string, string>()
                {
                    Key = $"any_key_{i}",
                    Value = $"any_value_{i}",
                });
                _outputHelper.WriteLine("produced message {0}", i);
            }

            await producer.FlushAsync();
        }

        Assert.Equal(5, activities.Where(x => x.OperationName == $"{topic} publish").Count());

        activities.Clear();

        using (var consumer = useKeyed
            ? host.Services.GetRequiredKeyedService<IConsumer<string, string>>(key)
            : host.Services.GetRequiredService<IConsumer<string, string>>())
        {
            consumer.Subscribe(topic);

            int j = 0;
            while (true)
            {
                var consumerResult = consumer.Consume();
                if (consumerResult == null)
                {
                    continue;
                }

                if (consumerResult.IsPartitionEOF)
                {
                    break;
                }

                _outputHelper.WriteLine("consumed message {0}", j);
                j++;
            }
        }

        Assert.Equal(5, activities.Where(x => x.OperationName == $"{topic} receive").Count());

        await host.StopAsync();
    }
}
