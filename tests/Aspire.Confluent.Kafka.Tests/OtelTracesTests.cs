// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.ConfluentKafka;
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
    [RequiresFeature(TestFeature.Testcontainers)]
    [InlineData(true)]
    [InlineData(false)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11820", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task EnsureTracesAreProducedAsync(bool useKeyed)
    {
        var activityExporter = new TestActivityExporter();
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var key = useKeyed ? "messaging" : null;
        var groupId = $"otel-group-{Guid.NewGuid()}";
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", _containerFixture?.Container?.GetBootstrapAddress()),
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaProducer<string, string>("messaging");
            builder.AddKeyedKafkaConsumer<string, string>("messaging", configureSettings: settings =>
            {
                settings.Config.GroupId = groupId;
                settings.Config.EnablePartitionEof = true;
                settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
            });
        }
        else
        {
            builder.AddKafkaProducer<string, string>("messaging");
            builder.AddKafkaConsumer<string, string>("messaging", configureSettings: settings =>
            {
                settings.Config.GroupId = groupId;
                settings.Config.EnablePartitionEof = true;
                settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest;
            });
        }

        builder.Services.AddOpenTelemetry().WithTracing(traceProviderBuilder =>
            traceProviderBuilder.AddProcessor(new SimpleActivityExportProcessor(activityExporter)));

        using var host = builder.Build();
        await host.StartAsync();

        using var consumer = useKeyed
            ? host.Services.GetRequiredKeyedService<IConsumer<string, string>>(key)
            : host.Services.GetRequiredService<IConsumer<string, string>>();
        var instrumentedConsumer = Assert.IsType<InstrumentedConsumer<string, string>>(consumer);
        using var admin = new DependentAdminClientBuilder(consumer.Handle).Build();
        var cluster = await admin.DescribeClusterAsync(new DescribeClusterOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        Assert.NotEmpty(cluster.ClusterId);

        // Discovery is asynchronous; wait for the shared cache before creating producer spans.
        var clusterIdTask = instrumentedConsumer.ClusterIdTask;
        Assert.NotNull(clusterIdTask);
        Assert.Equal(cluster.ClusterId, await clusterIdTask.WaitAsync(TimeSpan.FromSeconds(30)));

        string topic = $"otel-topic-{Guid.NewGuid()}";
        using (var producer = useKeyed
            ? host.Services.GetRequiredKeyedService<IProducer<string, string>>(key)
            : host.Services.GetRequiredService<IProducer<string, string>>())
        {
            for (int i = 0; i < 5; i++)
            {
                var message = new Message<string, string>()
                {
                    Key = $"any_key_{i}",
                    Value = $"any_value_{i}",
                };
                if (i < 3)
                {
                    producer.Produce(topic, message);
                }
                else
                {
                    await producer.ProduceAsync(topic, message);
                }
                _outputHelper.WriteLine("produced message {0}", i);
            }

            await producer.FlushAsync();
        }

        var sendActivities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic))
            .ToArray();
        Assert.Equal(5, sendActivities.Length);
        Assert.All(sendActivities, activity =>
        {
            Assert.Equal($"send {topic}", activity.OperationName);
            Assert.Equal(ActivityKind.Producer, activity.Kind);
        });

        consumer.Subscribe(topic);

        using var consumeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int j = 0;
        while (true)
        {
            var consumerResult = await consumer.ConsumeAndProcessMessageAsync(
                (_, _, _) => ValueTask.CompletedTask,
                consumeCancellation.Token);
            if (consumerResult is null)
            {
                continue;
            }

            if (consumerResult.IsPartitionEOF)
            {
                break;
            }

            Assert.Equal($"any_key_{j}", consumerResult.Message.Key);
            Assert.Equal($"any_value_{j}", consumerResult.Message.Value);
            _outputHelper.WriteLine("consumed message {0}", j);
            j++;
        }

        var topicActivities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic))
            .ToArray();
        Assert.All(topicActivities, activity =>
        {
            Assert.Equal(cluster.ClusterId, activity.GetTagItem("messaging.kafka.cluster.id"));
            Assert.Equal("0.3.0.0", activity.Source.Version);
            Assert.Equal("https://opentelemetry.io/schemas/1.44.0", activity.Source.TelemetrySchemaUrl);
        });
        var pollActivities = topicActivities
            .Where(activity => Equals(activity.GetTagItem("messaging.operation.name"), "poll"))
            .ToArray();
        Assert.Equal(5, pollActivities.Length);
        Assert.All(pollActivities, activity =>
        {
            Assert.Equal($"poll {topic}", activity.OperationName);
            Assert.Equal(ActivityKind.Client, activity.Kind);
        });

        var processActivities = topicActivities
            .Where(activity => Equals(activity.GetTagItem("messaging.operation.name"), "process"))
            .ToArray();
        Assert.Equal(5, processActivities.Length);
        Assert.All(processActivities, activity =>
        {
            Assert.Equal($"process {topic}", activity.OperationName);
            Assert.Equal(ActivityKind.Consumer, activity.Kind);
        });

        await host.StopAsync();
    }
}
