// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ virtual host resource that can be provisioned against a live broker.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, VirtualHostName = {VirtualHostName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQVirtualHostResource : RabbitMQProvisionableResource, IResourceWithConnectionString, IRabbitMQServerChild
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQVirtualHostResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="virtualHostName">The name of the virtual host.</param>
    /// <param name="parent">The RabbitMQ server resource associated with this virtual host.</param>
    public RabbitMQVirtualHostResource(string name, string virtualHostName, RabbitMQServerResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(virtualHostName);
        ArgumentNullException.ThrowIfNull(parent);

        VirtualHostName = virtualHostName;
        Parent = parent;
    }

    /// <summary>
    /// The broker name of the default RabbitMQ virtual host.
    /// </summary>
    internal const string DefaultVirtualHostName = "/";

    /// <summary>
    /// Gets the name of the virtual host as known to the broker (e.g. <c>/</c> for the default virtual host).
    /// </summary>
    public string VirtualHostName { get; }

    /// <summary>
    /// Gets a value indicating whether this is the default <c>/</c> virtual host.
    /// </summary>
    internal bool IsDefault => VirtualHostName == DefaultVirtualHostName;

    /// <summary>
    /// Gets the parent RabbitMQ server resource.
    /// </summary>
    public RabbitMQServerResource Parent { get; }

    /// <summary>
    /// Gets the AMQP connection string expression for this virtual host, including the vhost path segment.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            var builder = new ReferenceExpressionBuilder();
            builder.Append($"{Parent.ConnectionStringExpression}");
            if (!IsDefault)
            {
                builder.AppendLiteral("/");
                builder.AppendLiteral(Uri.EscapeDataString(VirtualHostName));
            }
            return builder.Build();
        }
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("Uri", ConnectionStringExpression),
            new("VirtualHost", ReferenceExpression.Create($"{VirtualHostName}")),
        ]);

    internal List<RabbitMQQueueResource> Queues { get; } = [];
    internal List<RabbitMQExchangeResource> Exchanges { get; } = [];
    internal List<RabbitMQShovelResource> Shovels { get; } = [];
    internal List<RabbitMQPolicyResource> Policies { get; } = [];

    /// <summary>
    /// Enumerates all child provisionable resources in this virtual host in provisioning order: policies, queues, exchanges, then shovels.
    /// </summary>
    internal IEnumerable<RabbitMQProvisionableResource> EnumerateChildren()
        => Policies.Cast<RabbitMQProvisionableResource>()
            .Concat(Queues.Cast<RabbitMQProvisionableResource>())
            .Concat(Exchanges.Cast<RabbitMQProvisionableResource>())
            .Concat(Shovels.Cast<RabbitMQProvisionableResource>());

    internal override async ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        // The default "/" virtual host always exists on a fresh broker, so there is nothing to create.
        if (!IsDefault)
        {
            await client.CreateVirtualHostAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        }
    }

    internal override async ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        // Never delete the default "/" virtual host — it is broker-owned and deleting it is destructive
        // beyond this resource's scope.
        if (!IsDefault)
        {
            await client.DeleteVirtualHostAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(EnumerateChildren().Select(child => child.DeleteAsync(client, cancellationToken).AsTask())).ConfigureAwait(false);
        }
    }

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        // The default "/" vhost has no management endpoint; probe via AMQP connect.
        // Named vhosts are probed via the management API existence endpoint.
        if (IsDefault)
        {
            var connected = await client.CanConnectAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
            return connected
                ? RabbitMQProbeResult.Healthy
                : RabbitMQProbeResult.Unhealthy($"Cannot connect to virtual host '{VirtualHostName}'.");
        }

        var exists = await client.VirtualHostExistsAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        return exists
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Virtual host '{VirtualHostName}' does not exist.");
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => this;
}
