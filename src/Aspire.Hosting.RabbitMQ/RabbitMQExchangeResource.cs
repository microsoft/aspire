// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a RabbitMQ exchange.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ExchangeName = {ExchangeName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQExchangeResource : Resource, IResourceWithParent<RabbitMQVirtualHostResource>, IResourceWithConnectionString, IRabbitMQBindableDestination, IRabbitMQProvisionable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQExchangeResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="exchangeName">The name of the exchange.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this exchange.</param>
    public RabbitMQExchangeResource(string name, string exchangeName, RabbitMQVirtualHostResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(exchangeName);
        ArgumentNullException.ThrowIfNull(parent);

        ExchangeName = exchangeName;
        Parent = parent;
    }

    /// <summary>
    /// Gets the name of the exchange.
    /// </summary>
    public string ExchangeName { get; }

    /// <summary>
    /// Gets the parent RabbitMQ virtual host resource.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

    /// <summary>
    /// Gets or sets the type of the exchange.
    /// </summary>
    public RabbitMQExchangeType ExchangeType { get; set; } = RabbitMQExchangeType.Direct;

    /// <summary>
    /// Gets or sets a value indicating whether the exchange is durable.
    /// </summary>
    public bool Durable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the exchange is auto-deleted.
    /// </summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    /// Gets the arguments for the exchange.
    /// </summary>
    public IDictionary<string, object?> Arguments { get; } = new Dictionary<string, object?>();

    internal List<RabbitMQBinding> Bindings { get; } = [];

    /// <summary>
    /// Gets the policies that apply to this exchange (populated by <c>BeforeStartEvent</c> handlers registered by <c>AddPolicy</c>).
    /// </summary>
    internal List<RabbitMQPolicyResource> AppliedPolicies { get; } = [];

    IEnumerable<IRabbitMQProvisionable> IRabbitMQProvisionable.HealthDependencies => AppliedPolicies;

    string IRabbitMQDestination.ProvisionedName => ExchangeName;
    RabbitMQVirtualHostResource IRabbitMQDestination.VirtualHost => Parent;
    RabbitMQDestinationKind IRabbitMQDestination.Kind => RabbitMQDestinationKind.Exchange;

    /// <summary>
    /// Gets the connection string expression for the RabbitMQ exchange.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.ConnectionStringExpression;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        Parent.CombineProperties([
            new("ExchangeName", ReferenceExpression.Create($"{ExchangeName}")),
        ]);

    /// <summary>
    /// Completed when this exchange has been declared AND all its bindings applied.
    /// Faulted if declaration or any binding failed.
    /// </summary>
    internal TaskCompletionSource ProvisioningComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    TaskCompletionSource IRabbitMQProvisionable.ProvisioningComplete => ProvisioningComplete;

    async Task IRabbitMQProvisionable.ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var typeString = ExchangeType.ToString().ToLowerInvariant();

        await client.DeclareExchangeAsync(
            Parent.VirtualHostName,
            ExchangeName,
            typeString,
            Durable,
            AutoDelete,
            Arguments.Count > 0 ? Arguments : null,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task ApplyBindingsAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        foreach (var binding in Bindings)
        {
            await ((IRabbitMQBindableDestination)binding.Destination).BindAsync(
                client,
                Parent.VirtualHostName,
                ExchangeName,
                binding.RoutingKey,
                binding.Arguments,
                cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask<RabbitMQProbeResult> IRabbitMQProvisionable.ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var exists = await client.ExchangeExistsAsync(Parent.VirtualHostName, ExchangeName, cancellationToken).ConfigureAwait(false);
        return exists
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Exchange '{ExchangeName}' does not exist in virtual host '{Parent.VirtualHostName}'.");
    }

    Task IRabbitMQBindableDestination.BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
        => client.BindExchangeAsync(vhost, sourceExchange, ExchangeName, routingKey, args, ct);
}
