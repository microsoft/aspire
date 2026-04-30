// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// A resource that represents a RabbitMQ virtual host.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, VirtualHostName = {VirtualHostName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQVirtualHostResource : Resource, IResourceWithParent<RabbitMQServerResource>, IResourceWithConnectionString, Provisioning.IRabbitMQProvisionable
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
    /// Gets the name of the virtual host.
    /// </summary>
    public string VirtualHostName { get; }

    /// <summary>
    /// Gets the parent RabbitMQ server resource.
    /// </summary>
    public RabbitMQServerResource Parent { get; }

    /// <summary>
    /// Gets the connection string expression for the RabbitMQ virtual host.
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

    internal TaskCompletionSource TopologyReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Task Provisioning.IRabbitMQProvisionable.ApplyAsync(Provisioning.IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ApplyAsync(client, cancellationToken);

    internal async Task ApplyAsync(Provisioning.IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        if (VirtualHostName != "/")
        {
            await client.CreateVirtualHostAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        }
    }
}
