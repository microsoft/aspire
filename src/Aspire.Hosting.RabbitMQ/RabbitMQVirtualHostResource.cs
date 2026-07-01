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
public class RabbitMQVirtualHostResource : RabbitMQProvisionableResource, IResourceWithParent<RabbitMQServerResource>, IResourceWithConnectionString, IRabbitMQServerChild
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
    /// Gets the name of the virtual host as known to the broker (e.g. <c>/</c> for the default virtual host).
    /// </summary>
    public string VirtualHostName { get; }

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
            if (VirtualHostName != "/")
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

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var connected = await client.CanConnectAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        return connected
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Cannot connect to virtual host '{VirtualHostName}'.");
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => this;
}
