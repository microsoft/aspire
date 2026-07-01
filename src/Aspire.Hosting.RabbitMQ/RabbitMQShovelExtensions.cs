// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for adding RabbitMQ shovel resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQShovelExtensions
{
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
    /// <ats-summary>Adds a shovel to a RabbitMQ virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQShovelResource> AddShovel<TSrc, TDest>(
        this IResourceBuilder<RabbitMQVirtualHostResource> vhost,
        [ResourceName] string name,
        IResourceBuilder<TSrc> source,
        IResourceBuilder<TDest> destination,
        string? shovelName = null)
        where TSrc : RabbitMQDestination
        where TDest : RabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(vhost);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (shovelName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(shovelName, nameof(shovelName));
        }

        var wireName = shovelName ?? name;
        if (vhost.Resource.Shovels.Any(s => s.ShovelName == wireName))
        {
            throw new DistributedApplicationException($"A shovel with the name '{wireName}' already exists in virtual host '{vhost.Resource.VirtualHostName}'.");
        }

        if (source.Resource.VirtualHost.Parent != vhost.Resource.Parent)
        {
            throw new DistributedApplicationException($"Cannot add shovel '{name}' because the source destination '{source.Resource.Name}' is on a different RabbitMQ server.");
        }

        if (destination.Resource.VirtualHost.Parent != vhost.Resource.Parent)
        {
            throw new DistributedApplicationException($"Cannot add shovel '{name}' because the destination '{destination.Resource.Name}' is on a different RabbitMQ server.");
        }

        var shovel = new RabbitMQShovelResource(name, wireName, vhost.Resource, source.Resource, destination.Resource);
        vhost.Resource.Shovels.Add(shovel);

        vhost.ApplicationBuilder.CreateResourceBuilder(vhost.Resource.Parent)
            .WithManagementPlugin()
            .WithPlugin(RabbitMQPlugin.Shovel)
            .WithPlugin(RabbitMQPlugin.ShovelManagement);

        var vhostResource = vhost.Resource;

        return RabbitMQBuilderExtensions.WithProvisionableHealthCheck(vhost.ApplicationBuilder.AddResource(shovel)
            .WithRelationship(source.Resource, "Source")
            .WithRelationship(destination.Resource, "Destination"))
            .WithIconName("ArrowMove")
            .WithRabbitMQProvisioning(
                dependencies: [
                    (vhostResource, WaitType.WaitUntilHealthy),
                    (shovel.Source, WaitType.WaitUntilHealthy),
                    (shovel.Destination, WaitType.WaitUntilHealthy)
                ],
                provisionAsync: async (s, client, _, ct) =>
                {
                    // Shovels run inside the broker container and connect to the broker via the
                    // internal AMQP port (5672). The host-mapped port in ConnectionStringExpression
                    // is not reachable from inside the container.
                    var server = s.Parent.Parent;
                    var containerUri = await server.ContainerUriExpression.GetValueAsync(ct).ConfigureAwait(false)
                        ?? throw new DistributedApplicationException($"Could not resolve container URI for shovel '{s.ShovelName}'.");

                    var vhostSuffix = s.Parent.VirtualHostName == "/"
                        ? string.Empty
                        : $"/{Uri.EscapeDataString(s.Parent.VirtualHostName)}";
                    var srcUri = containerUri + vhostSuffix;
                    var destUri = containerUri + vhostSuffix;

                    var ackModeString = s.AckMode switch
                    {
                        RabbitMQShovelAckMode.OnConfirm => "on-confirm",
                        RabbitMQShovelAckMode.OnPublish => "on-publish",
                        RabbitMQShovelAckMode.NoAck => "no-ack",
                        _ => "on-confirm"
                    };

                    var def = new RabbitMQShovelDefinitionValue
                    {
                        SrcUri = srcUri,
                        SrcQueue = s.Source.Kind == RabbitMQDestinationKind.Queue ? s.Source.ProvisionedName : null,
                        SrcExchange = s.Source.Kind == RabbitMQDestinationKind.Exchange ? s.Source.ProvisionedName : null,
                        DestUri = destUri,
                        DestQueue = s.Destination.Kind == RabbitMQDestinationKind.Queue ? s.Destination.ProvisionedName : null,
                        DestExchange = s.Destination.Kind == RabbitMQDestinationKind.Exchange ? s.Destination.ProvisionedName : null,
                        AckMode = ackModeString,
                        ReconnectDelay = s.ReconnectDelay.HasValue ? (int)s.ReconnectDelay.Value.TotalSeconds : null,
                        SrcDeleteAfter = s.SrcDeleteAfter?.ToString(CultureInfo.InvariantCulture)
                    };

                    await client.PutShovelAsync(
                        vhostResource.VirtualHostName,
                        s.ShovelName,
                        new RabbitMQShovelDefinition { Value = def },
                        ct).ConfigureAwait(false);
                });
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
    /// <ats-summary>Adds a shovel to the default '/' virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("addShovelOnServer", MethodName = "addShovel")]
    public static IResourceBuilder<RabbitMQShovelResource> AddShovel<TSrc, TDest>(
        this IResourceBuilder<RabbitMQServerResource> server,
        [ResourceName] string name,
        IResourceBuilder<TSrc> source,
        IResourceBuilder<TDest> destination,
        string? shovelName = null)
        where TSrc : RabbitMQDestination
        where TDest : RabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(server);
        return RabbitMQBuilderExtensions.GetOrAddDefaultVirtualHost(server).AddShovel(name, source, destination, shovelName);
    }

    /// <summary>
    /// Configures properties of a RabbitMQ shovel.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withShovelProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQShovelResource> WithProperties(this IResourceBuilder<RabbitMQShovelResource> builder, Action<RabbitMQShovelResource> configure)
        => RabbitMQBuilderExtensions.WithPropertiesCore(builder, configure);
}
