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

        logger.LogInformation("Provisioning RabbitMQ topology for server '{Server}' ({VirtualHostCount} virtual host(s)).", server.Name, server.VirtualHosts.Count);

        await Task.WhenAll(server.VirtualHosts.Select(vhost => ProvisionVirtualHostAsync(vhost, client, cancellationToken))).ConfigureAwait(false);

        logger.LogInformation("RabbitMQ topology provisioning complete for server '{Server}'.", server.Name);
    }

    private static async Task ProvisionVirtualHostAsync(RabbitMQVirtualHostResource vhost, IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        try
        {
            // Phase 1: create the virtual host (must complete before declaring entities)
            await vhost.ApplyAsync(client, cancellationToken).ConfigureAwait(false);

            // Phase 2: declare exchanges and queues in parallel (independent of each other)
            var declareTasks = vhost.Exchanges
                .Select(e => e.ApplyAsync(client, cancellationToken))
                .Concat(vhost.Queues.Select(q => q.ApplyAsync(client, cancellationToken)));
            await Task.WhenAll(declareTasks).ConfigureAwait(false);

            // Phase 3: apply bindings and shovels in parallel (both depend on phase 2)
            var bindAndShovelTasks = vhost.Exchanges
                .Select(e => e.ApplyBindingsAsync(client, cancellationToken))
                .Concat(vhost.Shovels.Select(s => s.ApplyAsync(client, cancellationToken)));
            await Task.WhenAll(bindAndShovelTasks).ConfigureAwait(false);

            vhost.TopologyReady.TrySetResult();
        }
        catch (Exception ex)
        {
            vhost.TopologyReady.TrySetException(ex);
            throw;
        }
    }
}
