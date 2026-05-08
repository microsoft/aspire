// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal sealed class RabbitMQTopologyProvisioner
{
    public static async Task ProvisionTopologyAsync(RabbitMQServerResource server, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (server.VirtualHosts.Count == 0)
        {
            return;
        }

        var client = serviceProvider.GetRequiredKeyedService<IRabbitMQProvisioningClient>(server.Name);
        var logger = serviceProvider.GetRequiredService<ILogger<RabbitMQTopologyProvisioner>>();
        var resourceLogger = serviceProvider.GetRequiredService<ResourceLoggerService>();
        var notifications = serviceProvider.GetRequiredService<ResourceNotificationService>();

        logger.LogInformation("Provisioning RabbitMQ topology for server '{Server}' ({VirtualHostCount} virtual host(s)).", server.Name, server.VirtualHosts.Count);

        await Task.WhenAll(server.VirtualHosts.Select(vhost => ProvisionVirtualHostAsync(vhost, client, logger, resourceLogger, notifications, cancellationToken))).ConfigureAwait(false);

        logger.LogInformation("RabbitMQ topology provisioning complete for server '{Server}'.", server.Name);
    }

    /// <summary>
    /// Provisions all entities within a single virtual host in ordered phases: virtual host creation, policies, queues and exchanges, then bindings and shovels.
    /// Each resource's <see cref="IRabbitMQProvisionable.ApplyAsync"/> owns its own failure signalling and never throws.
    /// </summary>
    private static async Task ProvisionVirtualHostAsync(RabbitMQVirtualHostResource vhost, IRabbitMQProvisioningClient client, ILogger logger, ResourceLoggerService resourceLogger, ResourceNotificationService notifications, CancellationToken cancellationToken)
    {
        await ((IRabbitMQProvisionable)vhost).ApplyAsync(client, notifications, resourceLogger, cancellationToken).ConfigureAwait(false);

        if (((IRabbitMQProvisionable)vhost).ProvisionedTask.IsFaulted)
        {
            // Children remain pending (Starting) — no cascade fault. The health check returns Degraded.
            logger.LogError(((IRabbitMQProvisionable)vhost).ProvisionedTask.Exception!.InnerException, "Failed to create virtual host '{VirtualHost}'.", vhost.VirtualHostName);
            return;
        }

        await Task.WhenAll(vhost.Policies.Select(p => ((IRabbitMQProvisionable)p).ApplyAsync(client, notifications, resourceLogger, cancellationToken))).ConfigureAwait(false);

        // Phase 2: queues and exchange declarations run in parallel.
        // Exchanges are not fully provisioned yet — bindings come in phase 3.
        var phase2Tasks = vhost.Queues
            .Select(q => ((IRabbitMQProvisionable)q).ApplyAsync(client, notifications, resourceLogger, cancellationToken))
            .Concat(vhost.Exchanges.Select(e => ((IRabbitMQProvisionable)e).ApplyAsync(client, notifications, resourceLogger, cancellationToken)));

        await Task.WhenAll(phase2Tasks).ConfigureAwait(false);

        // Phase 3: exchange bindings and shovels run in parallel.
        // Exchanges whose declaration faulted in phase 2 skip binding — their TCS is already faulted.
        var phase3Tasks = vhost.Exchanges
            .Select(e => ((IRabbitMQProvisionable)e).ProvisionedTask.IsFaulted
                ? Task.CompletedTask
                : e.ApplyBindingsAsync(client, resourceLogger, cancellationToken))
            .Concat(vhost.Shovels.Select(s => ((IRabbitMQProvisionable)s).ApplyAsync(client, notifications, resourceLogger, cancellationToken)));

        await Task.WhenAll(phase3Tasks).ConfigureAwait(false);
    }
}
