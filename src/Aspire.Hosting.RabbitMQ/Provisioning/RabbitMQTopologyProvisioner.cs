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

        await Task.WhenAll(server.VirtualHosts.Select(vhost => ProvisionVirtualHostAsync(vhost, client, logger, cancellationToken))).ConfigureAwait(false);

        logger.LogInformation("RabbitMQ topology provisioning complete for server '{Server}'.", server.Name);
    }

    private static async Task ProvisionVirtualHostAsync(RabbitMQVirtualHostResource vhost, IRabbitMQProvisioningClient client, ILogger logger, CancellationToken cancellationToken)
    {
        // Phase 1: create the virtual host (must complete before declaring entities)
        try
        {
            await vhost.ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            vhost.ProvisioningComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            // Vhost creation failed — fault the vhost TCS and cascade to all children,
            // since nothing can exist without the vhost.
            vhost.ProvisioningComplete.TrySetException(ex);
            foreach (var q in vhost.Queues)
            {
                q.ProvisioningComplete.TrySetException(ex);
            }

            foreach (var e in vhost.Exchanges)
            {
                e.ProvisioningComplete.TrySetException(ex);
            }

            foreach (var s in vhost.Shovels)
            {
                s.ProvisioningComplete.TrySetException(ex);
            }

            logger.LogError(ex, "Failed to create virtual host '{VirtualHost}'.", vhost.VirtualHostName);
            return;
        }

        // Phase 2: declare exchanges and queues in parallel (independent of each other).
        // Exchanges are NOT signalled here — they wait until bindings are applied in phase 3.
        // Each failure is captured into the entity's own TCS; we do not short-circuit siblings.
        var phase2Tasks = vhost.Queues
            .Select(q => ApplyAndSignalAsync(q, client, logger, cancellationToken))
            .Concat(vhost.Exchanges.Select(e => DeclareExchangeAsync(e, client, logger, cancellationToken)));

        await Task.WhenAll(phase2Tasks).ConfigureAwait(false);

        // Phase 3: apply bindings (on exchanges that declared successfully) and shovels in parallel.
        // Exchange is signalled after its bindings complete (or fail).
        var phase3Tasks = vhost.Exchanges
            .Select(e => ApplyBindingsAndSignalAsync(e, client, logger, cancellationToken))
            .Concat(vhost.Shovels.Select(s => ApplyAndSignalAsync(s, client, logger, cancellationToken)));

        await Task.WhenAll(phase3Tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a provisionable resource and signals its TCS. Captures failures into the TCS
    /// without rethrowing, so sibling tasks are not short-circuited.
    /// </summary>
    private static async Task ApplyAndSignalAsync(IRabbitMQProvisionable resource, IRabbitMQProvisioningClient client, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await resource.ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            resource.ProvisioningComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            resource.ProvisioningComplete.TrySetException(ex);
            logger.LogError(ex, "Failed to provision resource '{Resource}'.", resource.Name);
        }
    }

    /// <summary>
    /// Declares an exchange (phase 2). Does NOT signal <see cref="IRabbitMQProvisionable.ProvisioningComplete"/>
    /// yet — that happens in phase 3 after bindings are applied.
    /// Captures failures into the TCS so that <see cref="ApplyBindingsAndSignalAsync"/> can skip gracefully.
    /// </summary>
    private static async Task DeclareExchangeAsync(RabbitMQExchangeResource exchange, IRabbitMQProvisioningClient client, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await exchange.ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            // Do NOT signal ProvisioningComplete here — bindings must be applied first.
        }
        catch (Exception ex)
        {
            // Fault the TCS now so the health check can report immediately.
            exchange.ProvisioningComplete.TrySetException(ex);
            logger.LogError(ex, "Failed to declare exchange '{Exchange}'.", exchange.Name);
        }
    }

    /// <summary>
    /// Applies bindings for an exchange (phase 3) and then signals its TCS.
    /// If the exchange declaration already faulted in phase 2, skips binding and leaves the TCS faulted.
    /// </summary>
    private static async Task ApplyBindingsAndSignalAsync(RabbitMQExchangeResource exchange, IRabbitMQProvisioningClient client, ILogger logger, CancellationToken cancellationToken)
    {
        // If declaration failed in phase 2, the TCS is already faulted — nothing to do.
        if (exchange.ProvisioningComplete.Task.IsFaulted)
        {
            return;
        }

        try
        {
            await exchange.ApplyBindingsAsync(client, cancellationToken).ConfigureAwait(false);
            exchange.ProvisioningComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            exchange.ProvisioningComplete.TrySetException(ex);
            logger.LogError(ex, "Failed to apply bindings for exchange '{Exchange}'.", exchange.Name);
        }
    }
}
