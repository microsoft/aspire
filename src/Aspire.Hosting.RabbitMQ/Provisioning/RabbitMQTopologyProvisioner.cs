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

        logger.LogInformation("Provisioning RabbitMQ topology for server '{Server}' ({VirtualHostCount} virtual host(s)).", server.Name, server.VirtualHosts.Count);

        await Task.WhenAll(server.VirtualHosts.Select(vhost => ProvisionVirtualHostAsync(vhost, client, logger, resourceLogger, cancellationToken))).ConfigureAwait(false);

        logger.LogInformation("RabbitMQ topology provisioning complete for server '{Server}'.", server.Name);
    }

    /// <summary>
    /// Provisions all entities within a single virtual host in four ordered phases:
    /// <list type="number">
    ///   <item><b>Phase 1</b>: Create the virtual host. If this fails, all child TCSs are faulted immediately and provisioning stops for this vhost.</item>
    ///   <item><b>Phase 1.5</b>: Apply policies in parallel. Policies are applied before entities so they take effect at declaration time. Each failure is isolated to its own TCS.</item>
    ///   <item><b>Phase 2</b>: Declare queues and exchanges in parallel. Exchange TCSs are <em>not</em> signalled here — that happens after bindings in phase 3.</item>
    ///   <item><b>Phase 3</b>: Apply exchange bindings and create shovels in parallel. Exchange TCSs are signalled after their bindings complete (or fail).</item>
    /// </list>
    /// </summary>
    private static async Task ProvisionVirtualHostAsync(RabbitMQVirtualHostResource vhost, IRabbitMQProvisioningClient client, ILogger logger, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        try
        {
            await ((IRabbitMQProvisionable)vhost).ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            vhost.ProvisioningComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            vhost.ProvisioningComplete.TrySetException(ex);
            foreach (var child in vhost.EnumerateChildren())
            {
                child.ProvisioningComplete.TrySetException(ex);
            }

            logger.LogError(ex, "Failed to create virtual host '{VirtualHost}'.", vhost.VirtualHostName);
            return;
        }

        await Task.WhenAll(vhost.Policies.Select(p => ApplyAndSignalAsync(p, client, resourceLogger, cancellationToken))).ConfigureAwait(false);

        var phase2Tasks = vhost.Queues
            .Select(q => ApplyAndSignalAsync(q, client, resourceLogger, cancellationToken))
            .Concat(vhost.Exchanges.Select(e => DeclareExchangeAsync(e, client, resourceLogger, cancellationToken)));

        await Task.WhenAll(phase2Tasks).ConfigureAwait(false);

        var phase3Tasks = vhost.Exchanges
            .Select(e => ApplyBindingsAndSignalAsync(e, client, resourceLogger, cancellationToken))
            .Concat(vhost.Shovels.Select(s => ApplyAndSignalAsync(s, client, resourceLogger, cancellationToken)));

        await Task.WhenAll(phase3Tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a provisionable resource and signals its TCS. Captures failures into the TCS
    /// without rethrowing, so sibling tasks are not short-circuited.
    /// </summary>
    private static async Task ApplyAndSignalAsync(IRabbitMQProvisionable resource, IRabbitMQProvisioningClient client, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        try
        {
            await resource.ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            resource.ProvisioningComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            resource.ProvisioningComplete.TrySetException(ex);
            resourceLogger.GetLogger(resource.Name).LogError(ex, "Failed to provision resource '{Resource}'.", resource.Name);
        }
    }

    /// <summary>
    /// Declares an exchange (phase 2). Does NOT signal <see cref="IRabbitMQProvisionable.ProvisioningComplete"/>
    /// yet — that happens in phase 3 after bindings are applied.
    /// Captures failures into the TCS so that <see cref="ApplyBindingsAndSignalAsync"/> can skip gracefully.
    /// </summary>
    private static async Task DeclareExchangeAsync(RabbitMQExchangeResource exchange, IRabbitMQProvisioningClient client, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        try
        {
            await ((IRabbitMQProvisionable)exchange).ApplyAsync(client, cancellationToken).ConfigureAwait(false);
            // Do NOT signal ProvisioningComplete here — bindings must be applied first.
        }
        catch (Exception ex)
        {
            // Fault the TCS now so the health check can report immediately.
            exchange.ProvisioningComplete.TrySetException(ex);
            resourceLogger.GetLogger(exchange.Name).LogError(ex, "Failed to declare exchange '{Exchange}'.", exchange.Name);
        }
    }

    /// <summary>
    /// Applies bindings for an exchange (phase 3) and then signals its TCS.
    /// If the exchange declaration already faulted in phase 2, skips binding and leaves the TCS faulted.
    /// </summary>
    private static async Task ApplyBindingsAndSignalAsync(RabbitMQExchangeResource exchange, IRabbitMQProvisioningClient client, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
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
            resourceLogger.GetLogger(exchange.Name).LogError(ex, "Failed to apply bindings for exchange '{Exchange}'.", exchange.Name);
        }
    }
}
