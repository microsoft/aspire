// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for adding RabbitMQ virtual host resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQVirtualHostExtensions
{
    /// <summary>
    /// Adds a RabbitMQ virtual host to the server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="virtualHostName">The name of the virtual host. If not provided, defaults to the resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a RabbitMQ virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQVirtualHostResource> AddVirtualHost(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        string? virtualHostName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (virtualHostName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(virtualHostName, nameof(virtualHostName));
        }

        var vhostName = virtualHostName ?? name;
        if (builder.Resource.VirtualHosts.Any(v => v.VirtualHostName == vhostName))
        {
            throw new DistributedApplicationException($"A virtual host with the name '{vhostName}' already exists on server '{builder.Resource.Name}'.");
        }

        var vhost = new RabbitMQVirtualHostResource(name, vhostName, builder.Resource);

        builder.Resource.VirtualHosts.Add(vhost);

        if (vhostName != "/")
        {
            builder.WithManagementPlugin();
        }

        return RabbitMQBuilderExtensions.WithProvisionableHealthCheck(builder.ApplicationBuilder.AddResource(vhost))
            .WithIconName("Server")
            .WithRabbitMQProvisioning(
                dependencies: [(builder.Resource, WaitType.WaitUntilHealthy)],
                provisionAsync: async (v, client, _, ct) =>
                {
                    if (v.VirtualHostName != "/")
                    {
                        await client.CreateVirtualHostAsync(v.VirtualHostName, ct).ConfigureAwait(false);
                    }
                });
    }
}
