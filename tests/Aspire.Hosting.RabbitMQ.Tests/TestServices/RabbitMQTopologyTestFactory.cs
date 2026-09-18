// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

/// <summary>
/// Pure static factories shared across the RabbitMQ topology test suite. These helpers build
/// resource-graph objects and health-check infrastructure without requiring a live
/// <see cref="RabbitMQTopologyTestHost"/> instance.
/// </summary>
internal static class RabbitMQTopologyTestFactory
{
    /// <summary>
    /// Builds a server + named virtual host for unit tests. The password parameter resolves synchronously
    /// so that shovel <c>ContainerUriExpression</c> values resolve without endpoint references.
    /// </summary>
    public static (RabbitMQServerResource server, RabbitMQVirtualHostResource vhost) BuildVhost(string vhostName = "myvhost")
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        return (server, new RabbitMQVirtualHostResource(vhostName, vhostName, server));
    }

    /// <summary>Standard <see cref="HealthCheckContext"/> for health check tests.</summary>
    public static HealthCheckContext MakeHealthCheckContext()
        => new() { Registration = new HealthCheckRegistration("test", _ => null!, null, null) };

    /// <summary>
    /// Builds a <see cref="RabbitMQProvisionableHealthCheck"/> wired to the given server name.
    /// </summary>
    public static RabbitMQProvisionableHealthCheck MakeHealthCheck(
        RabbitMQProvisionableResource self,
        RabbitMQServerResource server,
        IRabbitMQProvisioningClient client,
        ResourceNotificationService notifications)
        => new(self, server.Name, client, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

    /// <summary>Publishes the server as Running so the health check's Stage 1 gate passes.</summary>
    public static Task PublishServerRunningAsync(ResourceNotificationService notifications, RabbitMQServerResource server)
        => notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Running });
}
