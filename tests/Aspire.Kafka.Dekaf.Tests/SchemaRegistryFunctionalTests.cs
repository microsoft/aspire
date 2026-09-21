// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using Aspire.Confluent.Kafka.Tests;
using Aspire.TestUtilities;
using Avro;
using Avro.Generic;
using Dekaf;
using Dekaf.Admin;
using Dekaf.Consumer;
using Dekaf.Producer;
using Dekaf.SchemaRegistry;
using Dekaf.SchemaRegistry.Avro;
using Dekaf.SchemaRegistry.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

[Collection("Kafka Broker collection")]
public class SchemaRegistryFunctionalTests(KafkaContainerFixture fixture)
{
    [Theory]
    [RequiresFeature(TestFeature.Testcontainers)]
    [InlineData(false)]
    [InlineData(true)]
    public Task JsonRoundTrip(bool keyed)
    {
        const string schema = """{"type":"object","properties":{"Text":{"type":"string"}},"required":["Text"]}""";
        return RoundTrip(keyed, new TestMessage("hello schema registry"),
            (producer, registry) => producer.UseJsonSchemaRegistry(registry, schema),
            (consumer, registry) => consumer.UseJsonSchemaRegistry(registry),
            message => Assert.Equal("hello schema registry", message.Text));
    }

    [Theory]
    [RequiresFeature(TestFeature.Testcontainers)]
    [InlineData(false)]
    [InlineData(true)]
    public Task AvroRoundTrip(bool keyed)
    {
        var schema = (RecordSchema)global::Avro.Schema.Parse("""{"type":"record","name":"Message","fields":[{"name":"text","type":"string"}]}""");
        var message = new GenericRecord(schema);
        message.Add("text", "hello schema registry");
        return RoundTrip(keyed, message,
            (producer, registry) => producer.UseAvroSchemaRegistry(registry),
            (consumer, registry) => consumer.UseAvroSchemaRegistry(registry),
            received => Assert.Equal("hello schema registry", received["text"]));
    }

    [Theory]
    [RequiresFeature(TestFeature.Testcontainers)]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProtobufRoundTrip(bool keyed)
        => RoundTrip(keyed, new StringValue { Value = "hello schema registry" },
            (producer, registry) => producer.UseProtobufSchemaRegistry(registry),
            (consumer, registry) => consumer.UseProtobufSchemaRegistry(registry),
            received => Assert.Equal("hello schema registry", received.Value));

    private async Task RoundTrip<T>(bool keyed, T message,
        Action<ProducerBuilder<string, T>, ISchemaRegistryClient> configureProducer,
        Action<ConsumerBuilder<string, T>, ISchemaRegistryClient> configureConsumer,
        Action<T> assertMessage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var topic = $"aspire-schema-{Guid.NewGuid():N}";
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = fixture.Container!.GetBootstrapAddress();
        builder.Configuration["ConnectionStrings:registry"] = "https://registry.example.com";
        using var handler = new SchemaRegistryTestHandler();

        // Use the real registry client and serializers with an in-memory HTTP transport.
        // Kafka still receives the actual schema-framed bytes, inspected independently below.
        if (keyed)
        {
            builder.AddKeyedDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        }
        else
        {
            builder.AddDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        }
        ISchemaRegistryClient GetRegistry(IServiceProvider services) => keyed
            ? services.GetRequiredKeyedService<ISchemaRegistryClient>("registry")
            : services.GetRequiredService<ISchemaRegistryClient>();

        builder.AddDekafKafkaProducer<string, T>("messaging", (services, producer) => configureProducer(producer, GetRegistry(services)));
        builder.AddDekafKafkaConsumer<string, T>("messaging", (services, consumer) =>
        {
            configureConsumer(consumer, GetRegistry(services));
            consumer.WithGroupId(topic).WithAutoOffsetReset(AutoOffsetReset.Earliest).SubscribeTo(topic);
        });
        builder.AddKeyedDekafKafkaConsumer<string, byte[]>("raw", settings => settings.ConnectionString = fixture.Container.GetBootstrapAddress(),
            consumer => consumer.WithGroupId($"{topic}-raw").WithAutoOffsetReset(AutoOffsetReset.Earliest).SubscribeTo(topic));
        builder.AddDekafKafkaAdminClient("messaging");

        using var host = builder.Build();
        var admin = host.Services.GetRequiredService<IAdminClient>();
        await admin.CreateTopicsAsync([new NewTopic { Name = topic }], cancellationToken: timeout.Token);
        try
        {
            await host.StartAsync(timeout.Token);
            var producer = host.Services.GetRequiredService<IKafkaProducer<string, T>>();
            var consumer = host.Services.GetRequiredService<IKafkaConsumer<string, T>>();
            var rawConsumer = host.Services.GetRequiredKeyedService<IKafkaConsumer<string, byte[]>>("raw");
            for (var i = 0; i < 2; i++)
            {
                await producer.ProduceAsync(topic, "key", message, timeout.Token);
                var received = await consumer.ConsumeOneAsync(TimeSpan.FromSeconds(30), timeout.Token);
                Assert.NotNull(received);
                Assert.Equal("key", received.Value.Key);
                assertMessage(received.Value.Value!);

                var raw = await rawConsumer.ConsumeOneAsync(TimeSpan.FromSeconds(30), timeout.Token);
                Assert.NotNull(raw);
                // Confluent's prefix is a magic byte followed by a big-endian 32-bit schema ID.
                // https://docs.confluent.io/platform/current/schema-registry/fundamentals/serdes-develop/index.html#wire-format
                Assert.Equal(0, raw.Value.Value![0]);
                Assert.Equal(SchemaRegistryTestHandler.SchemaId, BinaryPrimitives.ReadInt32BigEndian(raw.Value.Value.AsSpan(1, 4)));
            }

            Assert.Equal($"{topic}-value", handler.Subject);
            await Verify(handler.RegisteredSchema.GetRawText(), "json").UseParameters(keyed);
            Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Post));
            Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal));
            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(timeout.Token);
            Assert.Equal(HealthStatus.Healthy, report.Entries[keyed ? "Kafka.Dekaf_schema_registry_registry" : "Kafka.Dekaf_schema_registry"].Status);
        }
        finally
        {
            await host.StopAsync(timeout.Token);
            await admin.DeleteTopicsAsync([topic], cancellationToken: timeout.Token);
        }
    }
}
