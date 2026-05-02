// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding RabbitMQ resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQBuilderExtensions
{
    /// <summary>
    /// Adds a RabbitMQ container to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="RabbitMQContainerImageTags.Tag"/> tag of the <inheritdoc cref="RabbitMQContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="userName">The parameter used to provide the user name for the RabbitMQ resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the password for the RabbitMQ resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port that the underlying container is bound to when running locally.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a RabbitMQ container resource")]
    public static IResourceBuilder<RabbitMQServerResource> AddRabbitMQ(this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // don't use special characters in the password, since it goes into a URI
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var rabbitMq = new RabbitMQServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(rabbitMq, async (@event, ct) =>
        {
            connectionString = await rabbitMq.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{rabbitMq.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";

        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(
            rabbitMq.Name,
            (sp, _) => new RabbitMQProvisioningClient(rabbitMq, sp.GetRequiredService<ILogger<RabbitMQProvisioningClient>>()));

        builder.Eventing.Subscribe<ResourceReadyEvent>(rabbitMq, async (@event, ct) =>
        {
            await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(rabbitMq, @event.Services, ct).ConfigureAwait(false);
        });

        builder.Services.AddHealthChecks().AddRabbitMQ(async (sp) =>
        {
            // NOTE: Ensure that execution of this setup callback is deferred until after
            //       the container is built & started.
            var client = (RabbitMQProvisioningClient)sp.GetRequiredKeyedService<IRabbitMQProvisioningClient>(rabbitMq.Name);
            return await client.GetOrCreateConnectionAsync("/", default).ConfigureAwait(false);
        }, healthCheckKey);

        var rabbitmq = builder.AddResource(rabbitMq)
                              .WithImage(RabbitMQContainerImageTags.Image, RabbitMQContainerImageTags.Tag)
                              .WithImageRegistry(RabbitMQContainerImageTags.Registry)
                              .WithEndpoint(port: port, targetPort: 5672, name: RabbitMQServerResource.PrimaryEndpointName)
                              .WithEnvironment(context =>
                              {
                                  context.EnvironmentVariables["RABBITMQ_DEFAULT_USER"] = rabbitMq.UserNameReference;
                                  context.EnvironmentVariables["RABBITMQ_DEFAULT_PASS"] = rabbitMq.PasswordParameter;
                              })
                              .WithHealthCheck(healthCheckKey);

        return rabbitmq;
    }

    /// <summary>
    /// Adds a named volume for the data folder to a RabbitMQ container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data volume to the RabbitMQ container")]
    public static IResourceBuilder<RabbitMQServerResource> WithDataVolume(this IResourceBuilder<RabbitMQServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/lib/rabbitmq", isReadOnly)
                      .RunWithStableNodeName();
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a RabbitMQ container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data bind mount to the RabbitMQ container")]
    public static IResourceBuilder<RabbitMQServerResource> WithDataBindMount(this IResourceBuilder<RabbitMQServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/rabbitmq", isReadOnly)
                      .RunWithStableNodeName();
    }

    /// <summary>
    /// Configures the RabbitMQ container resource to enable the RabbitMQ management plugin.
    /// </summary>
    /// <remarks>
    /// This method only supports custom tags matching the default RabbitMQ ones for the corresponding management tag to be inferred automatically, e.g. <c>4</c>, <c>4.0-alpine</c>, <c>4.0.2-management-alpine</c>, etc.<br />
    /// Calling this method on a resource configured with an unrecognized image registry, name, or tag will result in a <see cref="DistributedApplicationException"/> being thrown.
    /// This version of the package defaults to the <inheritdoc cref="RabbitMQContainerImageTags.ManagementTag"/> tag of the <inheritdoc cref="RabbitMQContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">Thrown when the current container image and tag do not match the defaults for <see cref="RabbitMQServerResource"/>.</exception>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withManagementPlugin dispatcher export.")]
    public static IResourceBuilder<RabbitMQServerResource> WithManagementPlugin(this IResourceBuilder<RabbitMQServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithManagementPlugin(port: null);
    }

    /// <summary>
    /// Adds a RabbitMQ virtual host to the server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="virtualHostName">The name of the virtual host. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a RabbitMQ virtual host")]
    public static IResourceBuilder<RabbitMQVirtualHostResource> AddVirtualHost(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        string? virtualHostName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var vhostName = virtualHostName ?? name;
        var vhost = new RabbitMQVirtualHostResource(name, vhostName, builder.Resource);

        builder.Resource.VirtualHosts.Add(vhost);

        if (vhostName != "/")
        {
            builder.WithManagementPlugin();
        }

        var vhostBuilder = builder.ApplicationBuilder.AddResource(vhost);

        return vhostBuilder.WithProvisionableHealthCheck(builder.Resource.Name);
    }

    internal static RabbitMQVirtualHostResource GetOrAddDefaultVirtualHost(this IResourceBuilder<RabbitMQServerResource> server)
    {
        var defaultVhost = server.Resource.VirtualHosts.FirstOrDefault(v => v.VirtualHostName == "/");
        if (defaultVhost is null)
        {
            var builder = server.AddVirtualHost($"{server.Resource.Name}-default-vhost", "/");
            defaultVhost = builder.Resource;
        }
        return defaultVhost;
    }

    /// <summary>
    /// Adds a queue to a RabbitMQ virtual host.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue. If not provided, defaults to the resource name.</param>
    /// <param name="type">The type of the queue.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a queue to a RabbitMQ virtual host")]
    public static IResourceBuilder<RabbitMQQueueResource> AddQueue(
        this IResourceBuilder<RabbitMQVirtualHostResource> builder,
        [ResourceName] string name,
        string? queueName = null,
        RabbitMQQueueType type = RabbitMQQueueType.Classic)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var qName = queueName ?? name;
        if (builder.Resource.Queues.Any(q => q.QueueName == qName))
        {
            throw new DistributedApplicationException($"A queue with the name '{qName}' already exists in virtual host '{builder.Resource.VirtualHostName}'.");
        }

        var queue = new RabbitMQQueueResource(name, qName, builder.Resource) { QueueType = type };

        builder.Resource.Queues.Add(queue);

        var queueBuilder = builder.ApplicationBuilder.AddResource(queue);

        return queueBuilder.WithProvisionableHealthCheck(builder.Resource.Parent.Name);
    }

    /// <summary>
    /// Adds a queue to the default '/' virtual host of a RabbitMQ server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="queueName">The name of the queue. If not provided, defaults to the resource name.</param>
    /// <param name="type">The type of the queue.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addQueueOnServer", MethodName = "addQueue", Description = "Adds a queue to the default '/' virtual host")]
    public static IResourceBuilder<RabbitMQQueueResource> AddQueue(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        string? queueName = null,
        RabbitMQQueueType type = RabbitMQQueueType.Classic)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var vhost = builder.ApplicationBuilder.CreateResourceBuilder(builder.GetOrAddDefaultVirtualHost());
        return vhost.AddQueue(name, queueName, type);
    }

    /// <summary>
    /// Adds an exchange to a RabbitMQ virtual host.
    /// </summary>
    /// <param name="builder">The RabbitMQ virtual host resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="type">The type of the exchange.</param>
    /// <param name="exchangeName">The name of the exchange. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds an exchange to a RabbitMQ virtual host")]
    public static IResourceBuilder<RabbitMQExchangeResource> AddExchange(
        this IResourceBuilder<RabbitMQVirtualHostResource> builder,
        [ResourceName] string name,
        RabbitMQExchangeType type = RabbitMQExchangeType.Direct,
        string? exchangeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var exName = exchangeName ?? name;
        if (builder.Resource.Exchanges.Any(e => e.ExchangeName == exName))
        {
            throw new DistributedApplicationException($"An exchange with the name '{exName}' already exists in virtual host '{builder.Resource.VirtualHostName}'.");
        }

        var exchange = new RabbitMQExchangeResource(name, exName, builder.Resource) { ExchangeType = type };

        builder.Resource.Exchanges.Add(exchange);

        var exchangeBuilder = builder.ApplicationBuilder.AddResource(exchange);

        return exchangeBuilder.WithProvisionableHealthCheck(builder.Resource.Parent.Name);
    }

    /// <summary>
    /// Adds an exchange to the default '/' virtual host of a RabbitMQ server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="type">The type of the exchange.</param>
    /// <param name="exchangeName">The name of the exchange. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addExchangeOnServer", MethodName = "addExchange", Description = "Adds an exchange to the default '/' virtual host")]
    public static IResourceBuilder<RabbitMQExchangeResource> AddExchange(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        RabbitMQExchangeType type = RabbitMQExchangeType.Direct,
        string? exchangeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var vhost = builder.ApplicationBuilder.CreateResourceBuilder(builder.GetOrAddDefaultVirtualHost());
        return vhost.AddExchange(name, type, exchangeName);
    }

    /// <summary>
    /// Configures properties of a RabbitMQ queue.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withQueueProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQQueueResource> WithProperties(this IResourceBuilder<RabbitMQQueueResource> builder, Action<RabbitMQQueueResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource);
        return builder;
    }

    /// <summary>
    /// Configures properties of a RabbitMQ exchange.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withExchangeProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQExchangeResource> WithProperties(this IResourceBuilder<RabbitMQExchangeResource> builder, Action<RabbitMQExchangeResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource);
        return builder;
    }

    /// <summary>
    /// Configures properties of a RabbitMQ shovel.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withShovelProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQShovelResource> WithProperties(this IResourceBuilder<RabbitMQShovelResource> builder, Action<RabbitMQShovelResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource);
        return builder;
    }

    /// <summary>
    /// Adds a binding from an exchange to a destination.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <param name="exchange">The exchange resource builder.</param>
    /// <param name="destination">The destination resource builder.</param>
    /// <param name="routingKey">The routing key for the binding.</param>
    /// <param name="arguments">The arguments for the binding.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a binding from an exchange to a queue or another exchange")]
    public static IResourceBuilder<RabbitMQExchangeResource> WithBinding<TDestination>(
        this IResourceBuilder<RabbitMQExchangeResource> exchange,
        IResourceBuilder<TDestination> destination,
        string routingKey = "",
        IDictionary<string, object?>? arguments = null)
        where TDestination : Resource, IRabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (exchange.Resource.Parent != destination.Resource.VirtualHost)
        {
            throw new DistributedApplicationException($"Cannot bind exchange '{exchange.Resource.Name}' to destination '{destination.Resource.Name}' because they are in different virtual hosts.");
        }

        exchange.Resource.Bindings.Add(new RabbitMQBinding(destination.Resource, routingKey, arguments));
        return exchange.WithRelationship(destination.Resource, "Binding");
    }

    /// <summary>
    /// Adds a shovel to a RabbitMQ virtual host.
    /// </summary>
    /// <typeparam name="TSrc">The type of the source resource.</typeparam>
    /// <typeparam name="TDest">The type of the destination resource.</typeparam>
    /// <param name="vhost">The RabbitMQ virtual host resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="source">The source resource builder.</param>
    /// <param name="destination">The destination resource builder.</param>
    /// <param name="shovelName">The name of the shovel in RabbitMQ. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a shovel to a RabbitMQ virtual host")]
    public static IResourceBuilder<RabbitMQShovelResource> AddShovel<TSrc, TDest>(
        this IResourceBuilder<RabbitMQVirtualHostResource> vhost,
        [ResourceName] string name,
        IResourceBuilder<TSrc> source,
        IResourceBuilder<TDest> destination,
        string? shovelName = null)
        where TSrc : Resource, IRabbitMQDestination
        where TDest : Resource, IRabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(vhost);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var wireName = shovelName ?? name;
        if (vhost.Resource.Shovels.Any(s => s.ShovelName == wireName))
        {
            throw new DistributedApplicationException($"A shovel with the name '{wireName}' already exists in virtual host '{vhost.Resource.VirtualHostName}'.");
        }

        if (source.Resource.VirtualHost != vhost.Resource)
        {
            throw new DistributedApplicationException($"Cannot add shovel '{name}' to virtual host '{vhost.Resource.Name}' because the source destination '{source.Resource.Name}' is in a different virtual host.");
        }

        var shovel = new RabbitMQShovelResource(name, wireName, vhost.Resource, new RabbitMQShovelEndpoint(source.Resource), new RabbitMQShovelEndpoint(destination.Resource));
        vhost.Resource.Shovels.Add(shovel);

        var server = vhost.ApplicationBuilder.CreateResourceBuilder(vhost.Resource.Parent);
        server.WithManagementPlugin();
        server.WithPlugin(RabbitMQPlugin.Shovel);
        server.WithPlugin(RabbitMQPlugin.ShovelManagement);

        var builder = vhost.ApplicationBuilder.AddResource(shovel)
            .WithRelationship(source.Resource, "Source")
            .WithRelationship(destination.Resource, "Destination");

        return builder.WithProvisionableHealthCheck(vhost.Resource.Parent.Name);
    }

    /// <summary>
    /// Adds a shovel to the default '/' virtual host of a RabbitMQ server.
    /// </summary>
    /// <typeparam name="TSrc">The type of the source resource.</typeparam>
    /// <typeparam name="TDest">The type of the destination resource.</typeparam>
    /// <param name="server">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="source">The source resource builder.</param>
    /// <param name="destination">The destination resource builder.</param>
    /// <param name="shovelName">The name of the shovel in RabbitMQ. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addShovelOnServer", MethodName = "addShovel", Description = "Adds a shovel to the default '/' virtual host")]
    public static IResourceBuilder<RabbitMQShovelResource> AddShovel<TSrc, TDest>(
        this IResourceBuilder<RabbitMQServerResource> server,
        [ResourceName] string name,
        IResourceBuilder<TSrc> source,
        IResourceBuilder<TDest> destination,
        string? shovelName = null)
        where TSrc : Resource, IRabbitMQDestination
        where TDest : Resource, IRabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(server);
        var vhost = server.ApplicationBuilder.CreateResourceBuilder(server.GetOrAddDefaultVirtualHost());
        return vhost.AddShovel(name, source, destination, shovelName);
    }

    /// <summary>
    /// Enables a RabbitMQ plugin.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="plugin">The plugin to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Enables a RabbitMQ plugin")]
    public static IResourceBuilder<RabbitMQServerResource> WithPlugin(
        this IResourceBuilder<RabbitMQServerResource> builder,
        RabbitMQPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var pluginName = plugin switch
        {
            RabbitMQPlugin.Management => "rabbitmq_management",
            RabbitMQPlugin.ManagementAgent => "rabbitmq_management_agent",
            RabbitMQPlugin.Shovel => "rabbitmq_shovel",
            RabbitMQPlugin.ShovelManagement => "rabbitmq_shovel_management",
            RabbitMQPlugin.Federation => "rabbitmq_federation",
            RabbitMQPlugin.FederationManagement => "rabbitmq_federation_management",
            RabbitMQPlugin.Stream => "rabbitmq_stream",
            RabbitMQPlugin.StreamManagement => "rabbitmq_stream_management",
            RabbitMQPlugin.Mqtt => "rabbitmq_mqtt",
            RabbitMQPlugin.Stomp => "rabbitmq_stomp",
            RabbitMQPlugin.WebMqtt => "rabbitmq_web_mqtt",
            RabbitMQPlugin.WebStomp => "rabbitmq_web_stomp",
            RabbitMQPlugin.Prometheus => "rabbitmq_prometheus",
            RabbitMQPlugin.Amqp10 => "rabbitmq_amqp1_0",
            _ => throw new ArgumentOutOfRangeException(nameof(plugin), plugin, null)
        };
        return builder.WithPlugin(pluginName);
    }

    /// <summary>
    /// Enables a RabbitMQ plugin by name.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="pluginName">The name of the plugin to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withPluginByName", MethodName = "withPlugin", Description = "Enables a RabbitMQ plugin by name")]
    public static IResourceBuilder<RabbitMQServerResource> WithPlugin(
        this IResourceBuilder<RabbitMQServerResource> builder,
        string pluginName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);

        builder.WithAnnotation(new RabbitMQPluginAnnotation(pluginName));

        if (!builder.Resource.HasPluginFileCallback)
        {
            builder.Resource.HasPluginFileCallback = true;
            builder.WithContainerFiles("/etc/rabbitmq", (context, ct) =>
            {
                var plugins = new HashSet<string>(StringComparer.Ordinal)
                {
                    "rabbitmq_management",
                    "rabbitmq_management_agent",
                    "rabbitmq_web_dispatch",
                    "rabbitmq_prometheus"
                };

                foreach (var annotation in builder.Resource.Annotations.OfType<RabbitMQPluginAnnotation>())
                {
                    plugins.Add(annotation.PluginName);
                }

                var content = $"[{string.Join(",", plugins)}].";
                IEnumerable<ContainerFileSystemItem> items =
                [
                    new ContainerFile { Name = "enabled_plugins", Contents = content }
                ];
                return Task.FromResult(items);
            });
        }

        return builder;
    }

    [AspireExport("withManagementPlugin", Description = "Enables the RabbitMQ management plugin")]
    internal static IResourceBuilder<RabbitMQServerResource> WithManagementPluginForPolyglot(
        this IResourceBuilder<RabbitMQServerResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithManagementPlugin(port);
    }

    /// <inheritdoc cref="WithManagementPlugin(IResourceBuilder{RabbitMQServerResource})" />
    /// <param name="builder">The resource builder.</param>
    /// <param name="port">The host port that can be used to access the management UI page when running locally.</param>
    /// <remarks>
    /// <example>
    /// Use <see cref="WithManagementPlugin(IResourceBuilder{RabbitMQServerResource}, int?)"/> to specify a port to access the RabbitMQ management UI page.
    /// <code>
    /// var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    ///                       .WithDataVolume()
    ///                       .WithManagementPlugin(port: 15672);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withManagementPlugin dispatcher export.")]
    public static IResourceBuilder<RabbitMQServerResource> WithManagementPlugin(this IResourceBuilder<RabbitMQServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var handled = false;
        var containerAnnotations = builder.Resource.Annotations.OfType<ContainerImageAnnotation>().ToList();

        if (containerAnnotations.Count == 1
            && containerAnnotations[0].Registry is RabbitMQContainerImageTags.Registry
            && string.Equals(containerAnnotations[0].Image, RabbitMQContainerImageTags.Image, StringComparison.OrdinalIgnoreCase))
        {
            // Existing annotation is in a state we can update to enable the management plugin
            // See tag details at https://hub.docker.com/_/rabbitmq

            const string management = "management";
            const string alpine = "alpine";

            var annotation = containerAnnotations[0];
            var existingTag = annotation.Tag;

            if (string.IsNullOrEmpty(existingTag))
            {
                // Set to default tag with management
                annotation.Tag = RabbitMQContainerImageTags.ManagementTag;
                handled = true;
            }
            else if (existingTag.EndsWith(management, StringComparison.OrdinalIgnoreCase)
                     || existingTag.EndsWith($"{management}-{alpine}", StringComparison.OrdinalIgnoreCase))
            {
                // Already using the management tag
                handled = true;
            }
            else if (existingTag.EndsWith(alpine, StringComparison.OrdinalIgnoreCase))
            {
                if (existingTag.Length > alpine.Length)
                {
                    // Transform tag like "3.12-alpine" to "3.12-management-alpine"
                    var tagPrefix = existingTag[..existingTag.IndexOf($"-{alpine}")];
                    annotation.Tag = $"{tagPrefix}-{management}-{alpine}";
                }
                else
                {
                    // Transform tag "alpine" to "management-alpine"
                    annotation.Tag = $"{management}-{alpine}";
                }
                handled = true;
            }
            else if (IsVersion(existingTag))
            {
                // Tag is in version format so just append "-management"
                annotation.Tag = $"{existingTag}-{management}";
                handled = true;
            }
        }

        if (handled)
        {
            builder.WithHttpEndpoint(port: port, targetPort: 15672, name: RabbitMQServerResource.ManagementEndpointName);
            return builder;
        }

        throw new DistributedApplicationException($"Cannot configure the RabbitMQ resource '{builder.Resource.Name}' to enable the management plugin as it uses an unrecognized container image registry, name, or tag.");
    }

    /// <summary>
    /// Registers a <see cref="RabbitMQProvisionableHealthCheck"/> for the given resource and wires it
    /// up via <see cref="ResourceBuilderExtensions.WithHealthCheck{T}"/>.
    /// All RabbitMQ child resources (vhost, queue, exchange, shovel) use this single helper.
    /// </summary>
    private static IResourceBuilder<T> WithProvisionableHealthCheck<T>(
        this IResourceBuilder<T> builder,
        string serverName)
        where T : Resource, IRabbitMQProvisionable
    {
        var resource = builder.Resource;
        var healthCheckKey = $"{resource.Name}_check";

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            sp =>
            {
                var client = sp.GetRequiredKeyedService<IRabbitMQProvisioningClient>(serverName);
                return new RabbitMQProvisionableHealthCheck(resource, client);
            },
            failureStatus: null,
            tags: null));

        return builder.WithHealthCheck(healthCheckKey);
    }

    private static bool IsVersion(string tag)
    {
        // Must not be empty or null
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        // First char must be a digit
        if (!char.IsAsciiDigit(tag[0]))
        {
            return false;
        }

        // Last char must be digit
        if (!char.IsAsciiDigit(tag[^1]))
        {
            return false;
        }

        // If a single digit no more to check
        if (tag.Length == 1)
        {
            return true;
        }

        // Skip first char as we already checked it's a digit
        var lastCharIsDigit = true;
        for (var i = 1; i < tag.Length; i++)
        {
            var c = tag[i];

            if (!(char.IsAsciiDigit(c) || c == '.') // Interim chars must be digits or a period
                || !lastCharIsDigit && c == '.') // '.' can only follow a digit
            {
                return false;
            }

            lastCharIsDigit = char.IsAsciiDigit(c);
        }

        return true;
    }

    private static IResourceBuilder<RabbitMQServerResource> RunWithStableNodeName(this IResourceBuilder<RabbitMQServerResource> builder)
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.WithEnvironment(context =>
            {
                // Set a stable node name so queue storage is consistent between sessions
                var nodeName = $"{builder.Resource.Name}@localhost";
                context.EnvironmentVariables["RABBITMQ_NODENAME"] = nodeName;
            });
        }

        return builder;
    }
}
