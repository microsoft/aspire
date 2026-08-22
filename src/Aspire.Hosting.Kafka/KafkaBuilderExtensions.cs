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

    // Listener names used when the broker is not password protected. These are kept for backwards
    // compatibility with app models that opt out of authentication.
    private const string PlaintextExternalListenerName = "PLAINTEXT_HOST";
    private const string PlaintextInternalListenerName = "PLAINTEXT_INTERNAL";

    // Listener names used when the broker is password protected. These deliberately contain no
    // underscore: the Confluent image translates KAFKA_FOO_BAR into the foo.bar broker property, so an
    // underscore that is part of a listener name has to be escaped as a double underscore in
    // KAFKA_LISTENER_NAME_<LISTENER>_PLAIN_SASL_JAAS_CONFIG. Underscore free names avoid that ambiguity.
    private const string SaslExternalListenerName = "EXTERNAL";
    private const string SaslInternalListenerName = "INTERNAL";

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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with optional userName and password parameters instead.")]
    public static IResourceBuilder<KafkaServerResource> AddKafka(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return builder.AddKafka(name, port, null, null);
    }

    /// <summary>
    /// Adds a Kafka resource to the application. A container is used for local development.
    /// The broker is protected with SASL/PLAIN authentication using a generated password unless one is provided.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="KafkaContainerImageTags.Tag"/> tag of the <inheritdoc cref="KafkaContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency</param>
    /// <param name="port">The host port of Kafka broker.</param>
    /// <param name="userName">The parameter used to provide the SASL user name for the Kafka broker. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the SASL password for the Kafka broker. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KafkaServerResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KafkaServerResource> AddKafka(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // The password ends up inside a JAAS configuration string and inside the connection string, both of
        // which are quote sensitive, so restrict the generated value to alphanumeric characters.
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var kafka = new KafkaServerResource(name, userName?.Resource, passwordParameter);

        ProducerConfig? healthCheckConfiguration = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(kafka, async (@event, ct) =>
        {
            // The health check talks to the broker directly, so it needs the bootstrap servers and the
            // credentials rather than the connection string, which is not a bootstrap server list once
            // authentication is enabled.
            var bootstrapServers = await kafka.PrimaryEndpoint.Property(EndpointProperty.HostAndPort).GetValueAsync(ct).ConfigureAwait(false);

            if (bootstrapServers == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{kafka.Name}' resource but the connection string was null.");
            }

            var configuration = new ProducerConfig { BootstrapServers = bootstrapServers };

            if (kafka.PasswordParameter is not null)
            {
                configuration.SecurityProtocol = SecurityProtocol.SaslPlaintext;
                configuration.SaslMechanism = SaslMechanism.Plain;
                configuration.SaslUsername = await kafka.UserNameReference.GetValueAsync(ct).ConfigureAwait(false);
                configuration.SaslPassword = await kafka.PasswordParameter.GetValueAsync(ct).ConfigureAwait(false);
            }

            healthCheckConfiguration = configuration;
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
                options.Configuration = healthCheckConfiguration ?? throw new InvalidOperationException("Connection string is unavailable");
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
            .WithIconName("MailMultiple")
            .WithEnvironment(context => ConfigureKafkaContainer(context, kafka))
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Configures the SASL password used by the Kafka broker.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">The parameter used to provide the SASL password for the Kafka resource. If <see langword="null"/>, authentication is disabled and the broker listens in plaintext.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KafkaServerResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KafkaServerResource> WithPassword(this IResourceBuilder<KafkaServerResource> builder, IResourceBuilder<ParameterResource>? password)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.PasswordParameter = password?.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the SASL user name used by the Kafka broker.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="userName">The parameter used to provide the SASL user name for the Kafka resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KafkaServerResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KafkaServerResource> WithUserName(this IResourceBuilder<KafkaServerResource> builder, IResourceBuilder<ParameterResource> userName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(userName);

        builder.Resource.UserNameParameter = userName.Resource;
        return builder;
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
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
                .WithIconName("WindowDatabase")
                .WithHttpEndpoint(targetPort: KafkaUIPort)
                .ExcludeFromManifest();

            builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(kafkaUi, (e, ct) =>
            {
                var kafkaResources = builder.ApplicationBuilder.Resources.OfType<KafkaServerResource>();

                int i = 0;
                foreach (var kafkaResource in kafkaResources)
                {
                    var resource = kafkaResource;
                    int index = i;
                    kafkaUiBuilder.WithEnvironment(context => ConfigureKafkaUIContainer(context, resource, index));

                    i++;
                }

                return Task.CompletedTask;
            });

            configureContainer?.Invoke(kafkaUiBuilder);

            return builder;
        }

        static void ConfigureKafkaUIContainer(EnvironmentCallbackContext context, KafkaServerResource resource, int index)
        {
            var endpoint = resource.InternalEndpoint;

            var bootstrapServers = context.ExecutionContext.IsRunMode
                // In run mode, Kafka UI assumes Kafka is being accessed over a default Aspire container network and hardcodes the host as the Kafka resource name
                // This will need to be refactored once updated service discovery APIs are available
                ? ReferenceExpression.Create($"{endpoint.Resource.Name}:{endpoint.Property(EndpointProperty.TargetPort)}")
                : ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.HostAndPort)}");

            context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_NAME"] = endpoint.Resource.Name;
            context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_BOOTSTRAPSERVERS"] = bootstrapServers;

            if (resource.PasswordParameter is not null)
            {
                context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_PROPERTIES_SECURITY_PROTOCOL"] = "SASL_PLAINTEXT";
                context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_PROPERTIES_SASL_MECHANISM"] = "PLAIN";
                context.EnvironmentVariables[$"KAFKA_CLUSTERS_{index}_PROPERTIES_SASL_JAAS_CONFIG"] = BuildJaasConfig(resource);
            }
        }

    }

    /// <summary>
    /// Configures the host port that the KafkaUI resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for KafkaUI.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for KafkaUI.</returns>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

        // Only the two client facing listeners are protected. The KRaft controller listener and the
        // inter broker listener are bound to the loopback interface inside the container, so they stay PLAINTEXT.
        var saslEnabled = resource.PasswordParameter is not null;
        var externalListener = saslEnabled ? SaslExternalListenerName : PlaintextExternalListenerName;
        var internalListener = saslEnabled ? SaslInternalListenerName : PlaintextInternalListenerName;
        var clientProtocol = saslEnabled ? "SASL_PLAINTEXT" : "PLAINTEXT";

        // Define the default listeners + an internal listener for the container to broker communication
        context.EnvironmentVariables[$"KAFKA_LISTENERS"] = $"PLAINTEXT://localhost:29092,CONTROLLER://localhost:29093,{externalListener}://0.0.0.0:{KafkaBrokerPort},{internalListener}://0.0.0.0:{KafkaInternalBrokerPort}";
        // Defaults default listeners security protocol map + the client facing listeners protocol
        context.EnvironmentVariables["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = $"CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,{externalListener}:{clientProtocol},{internalListener}:{clientProtocol}";

        // primaryEndpoint is the endpoint that is exposed to the host machine
        var primaryEndpoint = resource.PrimaryEndpoint;
        // internalEndpoint is the endpoint that is used for communication between containers
        var internalEndpoint = resource.InternalEndpoint;

        var advertisedListeners = context.ExecutionContext.IsRunMode
            // In run mode, the internal listener assumes kafka is being accessed over a default Aspire container network and hardcodes the resource address
            // This will need to be refactored once updated service discovery APIs are available
            ? ReferenceExpression.Create($"PLAINTEXT://localhost:29092,{externalListener}://localhost:{primaryEndpoint.Property(EndpointProperty.Port)},{internalListener}://{resource.Name}:{internalEndpoint.Property(EndpointProperty.TargetPort)}")
            : ReferenceExpression.Create($"PLAINTEXT://{primaryEndpoint.Property(EndpointProperty.Host)}:29092,{externalListener}://{primaryEndpoint.Property(EndpointProperty.HostAndPort)},{internalListener}://{internalEndpoint.Property(EndpointProperty.HostAndPort)}");

        context.EnvironmentVariables["KAFKA_ADVERTISED_LISTENERS"] = advertisedListeners;

        if (saslEnabled)
        {
            // Authentication only. No authorizer is configured, so every authenticated client keeps full access.
            context.EnvironmentVariables["KAFKA_SASL_ENABLED_MECHANISMS"] = "PLAIN";

            var jaasConfig = BuildJaasConfig(resource);
            context.EnvironmentVariables[$"KAFKA_LISTENER_NAME_{externalListener}_PLAIN_SASL_JAAS_CONFIG"] = jaasConfig;
            context.EnvironmentVariables[$"KAFKA_LISTENER_NAME_{internalListener}_PLAIN_SASL_JAAS_CONFIG"] = jaasConfig;
        }
    }

    /// <summary>
    /// Builds the JAAS configuration declaring the single SASL/PLAIN user accepted by the broker.
    /// </summary>
    private static ReferenceExpression BuildJaasConfig(KafkaServerResource resource)
    {
        var userName = resource.UserNameReference;
        var password = resource.PasswordParameter!;

        // username/password are the credentials the broker presents when it acts as a client, user_<name>
        // declares the credentials the broker accepts from clients.
        return ReferenceExpression.Create(
            $"org.apache.kafka.common.security.plain.PlainLoginModule required username=\"{userName}\" password=\"{password}\" user_{userName}=\"{password}\";");
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
