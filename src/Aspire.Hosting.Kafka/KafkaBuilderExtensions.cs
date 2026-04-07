// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Confluent.Kafka;
using HealthChecks.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kafka resources to the application model.
/// </summary>
public static class KafkaBuilderExtensions
{
    private const int KafkaBrokerPort = 9092;
    private const int KafkaInternalBrokerPort = 9093;
    private const int KafkaUIPort = 8080;
    private const string Target = "/var/lib/kafka/data";

    /// <summary>
    /// Adds a Kafka resource to the application. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="KafkaContainerImageTags.Tag"/> tag of the <inheritdoc cref="KafkaContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency</param>
    /// <param name="port">The host port of Kafka broker.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KafkaServerResource}"/>.</returns>
    [AspireExport(Description = "Adds a Kafka container resource")]
    public static IResourceBuilder<KafkaServerResource> AddKafka(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var kafka = new KafkaServerResource(name);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(kafka, async (@event, ct) =>
        {
            connectionString = await kafka.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{kafka.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";

        // NOTE: We cannot use AddKafka here because it registers the health check as a singleton
        //       which means if you have multiple Kafka resources the factory callback will end
        //       up using the connection string of the last Kafka resource that was added. The
        //       client packages also have to work around this issue.
        //
        //       SEE: https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks/issues/2298
        var healthCheckRegistration = new HealthCheckRegistration(
            healthCheckKey,
            sp =>
            {
                var options = new KafkaHealthCheckOptions();
                options.Configuration = new ProducerConfig();
                options.Configuration.BootstrapServers = connectionString ?? throw new InvalidOperationException("Connection string is unavailable");
                return new KafkaHealthCheck(options);
            },
            failureStatus: default,
            tags: default);
        builder.Services.AddHealthChecks().Add(healthCheckRegistration);

        return builder.AddResource(kafka)
            .WithEndpoint(targetPort: KafkaBrokerPort, port: port, name: KafkaServerResource.PrimaryEndpointName)
            .WithEndpoint(targetPort: KafkaInternalBrokerPort, name: KafkaServerResource.InternalEndpointName)
            .WithImage(KafkaContainerImageTags.Image, KafkaContainerImageTags.Tag)
            .WithImageRegistry(KafkaContainerImageTags.Registry)
            .WithEnvironment(context => ConfigureKafkaContainer(context, kafka))
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a Kafka UI container to the application.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="KafkaContainerImageTags.KafkaUiTag"/> tag of the <inheritdoc cref="KafkaContainerImageTags.KafkaUiImage"/> container image.
    /// </remarks>
    /// <param name="builder">The Kafka server resource builder.</param>
    /// <param name="configureContainer">Configuration callback for KafkaUI container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KafkaServerResource}"/>.</returns>
    [AspireExport(Description = "Adds a Kafka UI container to manage the Kafka resource", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<KafkaServerResource> WithKafkaUI(this IResourceBuilder<KafkaServerResource> builder, Action<IResourceBuilder<KafkaUIContainerResource>>? configureContainer = null, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<KafkaUIContainerResource>().SingleOrDefault() is { } existingKafkaUIResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingKafkaUIResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "kafka-ui";

            var kafkaUi = new KafkaUIContainerResource(containerName);
            var kafkaUiBuilder = builder.ApplicationBuilder.AddResource(kafkaUi)
                .WithImage(KafkaContainerImageTags.KafkaUiImage, KafkaContainerImageTags.KafkaUiTag)
                .WithImageRegistry(KafkaContainerImageTags.Registry)
                .WithHttpEndpoint(targetPort: KafkaUIPort)
                .ExcludeFromManifest();

            builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(kafkaUi, (e, ct) =>
            {
                var kafkaResources = builder.ApplicationBuilder.Resources.OfType<KafkaServerResource>();

                int i = 0;
                foreach (var kafkaResource in kafkaResources)
                {
                    var endpoint = kafkaResource.InternalEndpoint;
                    int index = i;
                    kafkaUiBuilder.WithEnvironment(context => ConfigureKafkaUIContainer(context, endpoint, index));

                    i++;
                }

                return Task.CompletedTask;
            });

            configureContainer?.Invoke(kafkaUiBuilder);

            return builder;
        }

        static void ConfigureKafkaUIContainer(EnvironmentCallbackContext context, EndpointReference endpoint, int index)
        {
            var bootstrapServers = context.ExecutionContext.IsRunMode
                // In run mode, Kafka UI assumes Kafka is being accessed over a default Aspire container network and hardcodes the host as the Kafka resource name
                // This will need to be refactored once updated service discovery APIs are available
                ? ReferenceExpression.Create($"{endpoint.Resource.Name}:{endpoint.Property(EndpointProperty.TargetPort)}")
                : ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.HostAndPort)}");

            context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_NAME"] = endpoint.Resource.Name;
            context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_BOOTSTRAPSERVERS"] = bootstrapServers;
        }

    }

    /// <summary>
    /// Configures the host port that the KafkaUI resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for KafkaUI.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for KafkaUI.</returns>
    [AspireExport(Description = "Sets the host port for the Kafka UI container")]
    public static IResourceBuilder<KafkaUIContainerResource> WithHostPort(this IResourceBuilder<KafkaUIContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Kafka container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data volume to the Kafka container")]
    public static IResourceBuilder<KafkaServerResource> WithDataVolume(this IResourceBuilder<KafkaServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithEnvironment(ConfigureLogDirs)
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), Target, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Kafka container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data bind mount to the Kafka container")]
    public static IResourceBuilder<KafkaServerResource> WithDataBindMount(this IResourceBuilder<KafkaServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder
            .WithEnvironment(ConfigureLogDirs)
            .WithBindMount(source, Target, isReadOnly);
    }

    private static void ConfigureKafkaContainer(EnvironmentCallbackContext context, KafkaServerResource resource)
    {
        // confluentinc/confluent-local is a docker image that contains a Kafka broker started with KRaft to avoid pulling a separate image for ZooKeeper.
        // See https://github.com/confluentinc/kafka-images/blob/master/local/README.md.
        // When not explicitly set default configuration is applied.
        // See https://github.com/confluentinc/kafka-images/blob/master/local/include/etc/confluent/docker/configureDefaults for more details.

        // Define the default listeners + an internal listener for the container to broker communication
        context.EnvironmentVariables[$"KAFKA_LISTENERS"] = $"PLAINTEXT://localhost:29092,CONTROLLER://localhost:29093,PLAINTEXT_HOST://0.0.0.0:{KafkaBrokerPort},PLAINTEXT_INTERNAL://0.0.0.0:{KafkaInternalBrokerPort}";
        // Defaults default listeners security protocol map + the internal listener to be PLAINTEXT
        context.EnvironmentVariables["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,PLAINTEXT_HOST:PLAINTEXT,PLAINTEXT_INTERNAL:PLAINTEXT";

        // primaryEndpoint is the endpoint that is exposed to the host machine
        var primaryEndpoint = resource.PrimaryEndpoint;
        // internalEndpoint is the endpoint that is used for communication between containers
        var internalEndpoint = resource.InternalEndpoint;

        var advertisedListeners = context.ExecutionContext.IsRunMode
            // In run mode, PLAINTEXT_INTERNAL assumes kafka is being accessed over a default Aspire container network and hardcodes the resource address
            // This will need to be refactored once updated service discovery APIs are available
            ? ReferenceExpression.Create($"PLAINTEXT://localhost:29092,PLAINTEXT_HOST://localhost:{primaryEndpoint.Property(EndpointProperty.Port)},PLAINTEXT_INTERNAL://{resource.Name}:{internalEndpoint.Property(EndpointProperty.TargetPort)}")
            : ReferenceExpression.Create($"PLAINTEXT://{primaryEndpoint.Property(EndpointProperty.Host)}:29092,PLAINTEXT_HOST://{primaryEndpoint.Property(EndpointProperty.HostAndPort)},PLAINTEXT_INTERNAL://{internalEndpoint.Property(EndpointProperty.HostAndPort)}");

        context.EnvironmentVariables["KAFKA_ADVERTISED_LISTENERS"] = advertisedListeners;
    }

    /// <summary>
    /// Only need to call this if we want to persistent kafka data
    /// </summary>
    /// <param name="context"></param>
    private static void ConfigureLogDirs(EnvironmentCallbackContext context)
    {
        context.EnvironmentVariables["KAFKA_LOG_DIRS"] = Target;
    }
}
