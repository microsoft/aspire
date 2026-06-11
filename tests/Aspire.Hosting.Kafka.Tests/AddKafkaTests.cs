// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kafka.Tests;

public class AddKafkaTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddKafkaContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddKafka("kafka");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<KafkaServerResource>());
        Assert.Equal("kafka", containerResource.Name);

        var endpoints = containerResource.Annotations.OfType<EndpointAnnotation>();
        Assert.Equal(2, endpoints.Count());

        var primaryEndpoint = Assert.Single(endpoints, e => e.Name == "tcp");
        Assert.Equal(9092, primaryEndpoint.TargetPort);
        Assert.False(primaryEndpoint.IsExternal);
        Assert.Equal("tcp", primaryEndpoint.Name);
        Assert.Null(primaryEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, primaryEndpoint.Protocol);
        Assert.Equal("tcp", primaryEndpoint.Transport);
        Assert.Equal("tcp", primaryEndpoint.UriScheme);

        var internalEndpoint = Assert.Single(endpoints, e => e.Name == "internal");
        Assert.Equal(9093, internalEndpoint.TargetPort);
        Assert.False(internalEndpoint.IsExternal);
        Assert.Equal("internal", internalEndpoint.Name);
        Assert.Null(internalEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, internalEndpoint.Protocol);
        Assert.Equal("tcp", internalEndpoint.Transport);
        Assert.Equal("tcp", internalEndpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(KafkaContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(KafkaContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(KafkaContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public async Task KafkaCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddKafka("kafka")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<KafkaServerResource>()) as IResourceWithConnectionString;
        var connectionString = await connectionStringResource.GetConnectionStringAsync();

        Assert.Equal("localhost:27017", connectionString);
        Assert.Equal("{kafka.bindings.tcp.host}:{kafka.bindings.tcp.port}", connectionStringResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task VerifyManifest()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kafka = appBuilder.AddKafka("kafka");

        var manifest = await ManifestUtils.GetManifest(kafka.Resource);

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "{kafka.bindings.tcp.host}:{kafka.bindings.tcp.port}",
              "image": "{{KafkaContainerImageTags.Registry}}/{{KafkaContainerImageTags.Image}}:{{KafkaContainerImageTags.Tag}}",
              "env": {
                "KAFKA_LISTENERS": "PLAINTEXT://localhost:29092,CONTROLLER://localhost:29093,PLAINTEXT_HOST://0.0.0.0:9092,PLAINTEXT_INTERNAL://0.0.0.0:9093",
                "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP": "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,PLAINTEXT_HOST:PLAINTEXT,PLAINTEXT_INTERNAL:PLAINTEXT",
                "KAFKA_ADVERTISED_LISTENERS": "PLAINTEXT://{kafka.bindings.tcp.host}:29092,PLAINTEXT_HOST://{kafka.bindings.tcp.host}:{kafka.bindings.tcp.port},PLAINTEXT_INTERNAL://{kafka.bindings.internal.host}:{kafka.bindings.internal.port}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 9092
                },
                "internal": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 9093
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task WithDataVolumeConfigureCorrectEnvironment()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kafka = appBuilder.AddKafka("kafka")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017))
            .WithDataVolume("kafka-data");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafka.Resource);

        var volumeAnnotation = kafka.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal("kafka-data", volumeAnnotation.Source);
        Assert.Equal("/var/lib/kafka/data", volumeAnnotation.Target);
        Assert.Contains(config, kvp => kvp.Key == "KAFKA_LOG_DIRS" && kvp.Value == "/var/lib/kafka/data");
    }

    [Fact]
    public async Task WithDataBindConfigureCorrectEnvironment()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kafka = appBuilder.AddKafka("kafka")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017))
            .WithDataBindMount("kafka-data");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafka.Resource);

        var volumeAnnotation = kafka.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        Assert.Equal(Path.Combine(appBuilder.AppHostDirectory, "kafka-data"), volumeAnnotation.Source);
        Assert.Equal("/var/lib/kafka/data", volumeAnnotation.Target);
        Assert.Contains(config, kvp => kvp.Key == "KAFKA_LOG_DIRS" && kvp.Value == "/var/lib/kafka/data");
    }

    public static TheoryData<string?, string, int?> WithKafkaUIAddsAnUniqueContainerSetsItsNameAndInvokesConfigurationCallbackTestVariations()
    {
        return new()
        {
            { "kafka-ui", "kafka-ui", 8081 },
            { null, "kafka-ui", 8081 },
            { "kafka-ui", "kafka-ui", null },
            { null, "kafka-ui", null },
        };
    }

    [Theory]
    [MemberData(nameof(WithKafkaUIAddsAnUniqueContainerSetsItsNameAndInvokesConfigurationCallbackTestVariations))]
    public void WithKafkaUIAddsAnUniqueContainerSetsItsNameAndInvokesConfigurationCallback(string? containerName, string expectedContainerName, int? port)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var configureContainerInvocations = 0;
        Action<IResourceBuilder<KafkaUIContainerResource>> kafkaUIConfigurationCallback = kafkaUi =>
        {
            kafkaUi.WithHostPort(port);
            configureContainerInvocations++;
        };
        builder.AddKafka("kafka1").WithKafkaUI(configureContainer: kafkaUIConfigurationCallback, containerName: containerName);
        builder.AddKafka("kafka2").WithKafkaUI();

        Assert.Single(builder.Resources.OfType<KafkaUIContainerResource>());
        var kafkaUiResource = Assert.Single(builder.Resources, r => r.Name == expectedContainerName);
        Assert.Equal(1, configureContainerInvocations);
        var kafkaUiEndpoint = kafkaUiResource.Annotations.OfType<EndpointAnnotation>().Single();
        Assert.Equal(8080, kafkaUiEndpoint.TargetPort);
        Assert.Equal(port, kafkaUiEndpoint.Port);
    }

    [Fact]
    public async Task KafkaEnvironmentCallbackIsIdempotent()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kafka = appBuilder.AddKafka("kafka")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017));

        // Call GetEnvironmentVariablesAsync multiple times to ensure callbacks are idempotent
        var config1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafka.Resource);
        var config2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafka.Resource);

        // Both calls should succeed and return the same values
        Assert.Equal(config1.Count, config2.Count);
        Assert.Contains(config1, kvp => kvp.Key == "KAFKA_LISTENERS");
        Assert.Contains(config2, kvp => kvp.Key == "KAFKA_LISTENERS");
        Assert.Equal(
            config1.First(kvp => kvp.Key == "KAFKA_LISTENERS").Value,
            config2.First(kvp => kvp.Key == "KAFKA_LISTENERS").Value);
    }

    [Fact]
    public async Task KafkaUIEnvironmentCallbackIsIdempotent()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var kafka = appBuilder.AddKafka("kafka1")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 27017))
            .WithKafkaUI();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var kafkaUiResource = Assert.Single(appModel.Resources.OfType<KafkaUIContainerResource>());

        // Trigger the BeforeResourceStartedEvent to add environment callbacks
        await appBuilder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(kafkaUiResource, app.Services),
            EventDispatchBehavior.BlockingSequential);

        // Call GetEnvironmentVariablesAsync multiple times to ensure callbacks are idempotent
        var config1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafkaUiResource);
        var config2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(kafkaUiResource);

        // Both calls should succeed and return the same values
        Assert.Equal(config1.Count, config2.Count);
        Assert.Contains(config1, kvp => kvp.Key == "KAFKA_CLUSTERS_0_NAME");
        Assert.Contains(config2, kvp => kvp.Key == "KAFKA_CLUSTERS_0_NAME");
        Assert.Equal("kafka1", config1.First(kvp => kvp.Key == "KAFKA_CLUSTERS_0_NAME").Value);
        Assert.Equal("kafka1", config2.First(kvp => kvp.Key == "KAFKA_CLUSTERS_0_NAME").Value);
    }
}
