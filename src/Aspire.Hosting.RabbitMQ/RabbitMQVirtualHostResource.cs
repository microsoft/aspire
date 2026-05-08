// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a RabbitMQ virtual host.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, VirtualHostName = {VirtualHostName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQVirtualHostResource : Resource, IResourceWithParent<RabbitMQServerResource>, IResourceWithConnectionString, IRabbitMQProvisionable, IRabbitMQServerChild
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
    /// <remarks>
    /// This is always a compile-time literal string. Virtual host names are plain
    /// <see langword="string"/> constructor parameters and cannot be driven by a
    /// <see cref="ParameterResource"/>.
    /// </remarks>
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
    internal List<RabbitMQPolicyResource> Policies { get; } = [];

    /// <summary>
    /// Enumerates all child provisionable resources in this virtual host (policies, queues, exchanges, shovels).
    /// </summary>
    internal IEnumerable<IRabbitMQProvisionable> EnumerateChildren()
        => Policies.Cast<IRabbitMQProvisionable>()
            .Concat(Queues)
            .Concat(Exchanges)
            .Concat(Shovels);

    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Task IRabbitMQProvisionable.ProvisionedTask => _tcs.Task;

    async Task IRabbitMQProvisionable.ApplyAsync(IRabbitMQProvisioningClient client, ResourceNotificationService notifications, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);
        try
        {
            if (VirtualHostName != "/")
            {
                await client.CreateVirtualHostAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
            }

            _tcs.TrySetResult();
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Running }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
            resourceLogger.GetLogger(Name).LogError(ex, "Failed to create virtual host '{VirtualHost}'.", VirtualHostName);
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
        }
    }

    async ValueTask<RabbitMQProbeResult> IRabbitMQProvisionable.ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var connected = await client.CanConnectAsync(VirtualHostName, cancellationToken).ConfigureAwait(false);
        return connected
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Cannot connect to virtual host '{VirtualHostName}'.");
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => this;
}
