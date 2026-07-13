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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

        builder.Services.AddHealthChecks().AddRabbitMQ(async (sp) =>
        {
            // NOTE: Ensure that execution of this setup callback is deferred until after
            //       the container is built & started.
            // The cast to RabbitMQProvisioningClient is intentional: AddRabbitMQ (the AspNetCore health-check
            // extension) requires an IConnection, which is a RabbitMQ.Client type. Exposing IConnection on
            // IRabbitMQProvisioningClient would leak the client library into the internal facade, so we keep
            // the cast here — the concrete type is internal and registered by us.
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
    /// Enables a RabbitMQ plugin.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="plugin">The plugin to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "RabbitMQPlugin is a C# enum — not usable from polyglot hosts. Use the string overload (withPlugin) instead.")]
    public static IResourceBuilder<RabbitMQServerResource> WithPlugin(
        this IResourceBuilder<RabbitMQServerResource> builder,
        RabbitMQPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithPlugin(plugin.ToPluginName());
    }

    /// <summary>
    /// Enables a RabbitMQ plugin by name.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="pluginName">The name of the plugin to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Enables a RabbitMQ plugin by name.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withPluginByName", MethodName = "withPlugin")]
    public static IResourceBuilder<RabbitMQServerResource> WithPlugin(
        this IResourceBuilder<RabbitMQServerResource> builder,
        string pluginName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);

        builder.Resource.EnabledPlugins.Add(pluginName);

        if (!builder.Resource.HasPluginFileCallback)
        {
            builder.Resource.HasPluginFileCallback = true;
            builder.WithContainerFiles("/etc/rabbitmq", (context, ct) =>
            {
                var plugins = builder.Resource.EnabledPlugins
                    .OrderBy(x => x, StringComparer.Ordinal);

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

    /// <summary>
    /// Enables the RabbitMQ management plugin.
    /// </summary>
    [AspireExport("withManagementPlugin")]
    internal static IResourceBuilder<RabbitMQServerResource> WithManagementPluginForPolyglot(
        this IResourceBuilder<RabbitMQServerResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithManagementPlugin(port);
    }

    /// <inheritdoc cref="WithManagementPlugin(IResourceBuilder{RabbitMQServerResource})" />
    /// <param name="builder">The resource builder.</param>
    /// <param name="port">The host port used to access the management UI when running locally.</param>
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
            if (!builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => e.Name == RabbitMQServerResource.ManagementEndpointName))
            {
                builder.WithHttpEndpoint(port: port, targetPort: 15672, name: RabbitMQServerResource.ManagementEndpointName);
            }

            // Register the plugins that the management image bundles so that the enabled_plugins file
            // reflects the full set when WithPlugin is also called.
            builder.WithPlugin(RabbitMQPlugin.Management);
            builder.WithPlugin(RabbitMQPlugin.ManagementAgent);
            builder.WithPlugin(RabbitMQPlugin.WebDispatch);
            builder.WithPlugin(RabbitMQPlugin.Prometheus);

            return builder;
        }

        throw new DistributedApplicationException($"Cannot configure the RabbitMQ resource '{builder.Resource.Name}' to enable the management plugin as it uses an unrecognized container image registry, name, or tag.");
    }

    internal static IResourceBuilder<RabbitMQVirtualHostResource> GetOrAddDefaultVirtualHost(this IResourceBuilder<RabbitMQServerResource> server)
    {
        var defaultVhost = server.Resource.VirtualHosts.FirstOrDefault(v => v.IsDefault);
        if (defaultVhost is not null)
        {
            return server.ApplicationBuilder.CreateResourceBuilder(defaultVhost);
        }

        return server.AddVirtualHost($"{server.Resource.Name}-default-vhost", RabbitMQVirtualHostResource.DefaultVirtualHostName);
    }

    /// <summary>
    /// Registers a provisioning health check for the given resource and wires it up.
    /// The server name is derived from <see cref="IRabbitMQServerChild.VirtualHost"/> so it
    /// does not need to be passed explicitly at every call site.
    /// </summary>
    internal static IResourceBuilder<T> WithProvisionableHealthCheck<T>(
        this IResourceBuilder<T> builder)
        where T : RabbitMQProvisionableResource, IRabbitMQServerChild
    {
        var resource = builder.Resource;
        var serverName = resource.VirtualHost.Parent.Name;
        var healthCheckKey = $"{resource.Name}_check";

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            sp =>
            {
                var client = sp.GetRequiredKeyedService<IRabbitMQProvisioningClient>(serverName);
                var notifications = sp.GetRequiredService<ResourceNotificationService>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<RabbitMQProvisionableHealthCheck>();
                // Pass serverName so the health check can short-circuit when the broker is not Running,
                // avoiding a live probe call (which would hang until the HTTP timeout) against a dead broker.
                return new RabbitMQProvisionableHealthCheck(resource, serverName, client, notifications, logger);
            },
            failureStatus: null,
            tags: null));

        return builder.WithHealthCheck(healthCheckKey);
    }

    internal static IResourceBuilder<T> WithPropertiesCore<T>(IResourceBuilder<T> builder, Action<T> configure)
        where T : Resource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource);
        return builder;
    }

    /// <summary>
    /// Resolves which queues and exchanges in <paramref name="vhost"/> match <paramref name="policy"/>
    /// and wires up the applied-policies lists and dashboard relationships.
    /// Exposed internally for testing.
    /// </summary>
    internal static void ResolveAndApplyPolicyMatches(
        RabbitMQPolicyResource policy,
        RabbitMQVirtualHostResource vhost,
        IResourceBuilder<RabbitMQPolicyResource> policyBuilder)
    {
        foreach (var queue in vhost.Queues)
        {
            if (policy.AppliesTo(queue.QueueName, RabbitMQDestinationKind.Queue))
            {
                queue.AppliedPolicies.Add(policy);
                policyBuilder.WithRelationship(queue, "Policy");
            }
        }

        foreach (var exchange in vhost.Exchanges)
        {
            if (policy.AppliesTo(exchange.ExchangeName, RabbitMQDestinationKind.Exchange))
            {
                exchange.AppliedPolicies.Add(policy);
                policyBuilder.WithRelationship(exchange, "Policy");
            }
        }
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
