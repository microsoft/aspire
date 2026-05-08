// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ exchange resource that is declared on the broker during provisioning.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ExchangeName = {ExchangeName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQExchangeResource : RabbitMQDestination, IResourceWithConnectionString, IRabbitMQProvisionable, IResourceWithExchangeArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQExchangeResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="exchangeName">The name of the exchange.</param>
    /// <param name="virtualHost">The RabbitMQ virtual host resource associated with this exchange.</param>
    /// <param name="exchangeType">The type of the exchange. Defaults to <see cref="RabbitMQExchangeType.Direct"/>.</param>
    public RabbitMQExchangeResource(string name, string exchangeName, RabbitMQVirtualHostResource virtualHost, RabbitMQExchangeType exchangeType = RabbitMQExchangeType.Direct) : base(name, virtualHost)
    {
        ArgumentNullException.ThrowIfNull(exchangeName);

        ExchangeName = exchangeName;
        ExchangeType = exchangeType;
    }

    /// <summary>
    /// Gets the name of the exchange.
    /// </summary>
    public string ExchangeName { get; }

    /// <summary>
    /// Gets the routing algorithm used by this exchange. Set via the <c>type</c> parameter of <c>AddExchange</c>.
    /// </summary>
    public RabbitMQExchangeType ExchangeType { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the exchange is durable.
    /// </summary>
    public bool Durable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the exchange is auto-deleted.
    /// </summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    /// Gets the exchange arguments for this exchange declaration, such as the alternate exchange for unroutable messages.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQBuilderExtensions.WithExchangeArguments{T}"/> to configure these settings.
    /// </remarks>
    public RabbitMQExchangeArguments ExchangeArguments { get; } = new();

    internal List<RabbitMQBinding> Bindings { get; } = [];

    /// <summary>
    /// Gets the policies that apply to this exchange, resolved at startup from matching <c>AddPolicy</c> calls on the parent virtual host.
    /// </summary>
    internal List<RabbitMQPolicyResource> AppliedPolicies { get; } = [];

    IEnumerable<IRabbitMQProvisionable> IRabbitMQProvisionable.HealthDependencies
    {
        get
        {
            foreach (var policy in AppliedPolicies)
            {
                yield return policy;
            }

            // The alternate exchange must be provisioned before this exchange's health is meaningful.
            if (ExchangeArguments.AlternateExchange is { } ae)
            {
                yield return ae;
            }
        }
    }

    /// <inheritdoc/>
    public override string ProvisionedName => ExchangeName;

    /// <inheritdoc/>
    public override RabbitMQDestinationKind Kind => RabbitMQDestinationKind.Exchange;

    /// <summary>
    /// Gets the connection string properties for this exchange, including the exchange name.
    /// </summary>
    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        VirtualHost.CombineProperties([
            new("ExchangeName", ReferenceExpression.Create($"{ExchangeName}")),
        ]);

    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Task IRabbitMQProvisionable.ProvisionedTask => _tcs.Task;

    /// <summary>
    /// Declares the exchange on the broker and publishes <c>Running</c>.
    /// </summary>
    /// <remarks>
    /// The provisioned task is not signalled here — that happens after bindings are applied in <see cref="ApplyBindingsAsync"/>.
    /// On failure, faults the provisioned task and publishes <c>FailedToStart</c>.
    /// </remarks>
    async Task IRabbitMQProvisionable.ApplyAsync(IRabbitMQProvisioningClient client, ResourceNotificationService notifications, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);
        try
        {
            var typeString = ExchangeType.ToString().ToLowerInvariant();
            var args = new Dictionary<string, object?>();

            ExchangeArguments.FlattenInto(args, $"Exchange '{ExchangeName}'");

            await client.DeclareExchangeAsync(
                VirtualHost.VirtualHostName,
                ExchangeName,
                typeString,
                Durable,
                AutoDelete,
                args.Count > 0 ? args : null,
                cancellationToken).ConfigureAwait(false);

            // Exchange IS running — it exists on the broker. Bindings are phase 3.
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Running }).ConfigureAwait(false);
            // ProvisionedTask stays pending until ApplyBindingsAsync completes.
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
            resourceLogger.GetLogger(Name).LogError(ex, "Failed to declare exchange '{Exchange}'.", ExchangeName);
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies all bindings for this exchange and signals the provisioned task.
    /// </summary>
    /// <remarks>
    /// On success, <see cref="IRabbitMQProvisionable.ProvisionedTask"/> completes and the lifecycle stays <c>Running</c>.
    /// On failure, the provisioned task is faulted but the lifecycle stays <c>Running</c> because the exchange itself was declared successfully.
    /// </remarks>
    internal async Task ApplyBindingsAsync(IRabbitMQProvisioningClient client, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var binding in Bindings)
            {
                await binding.Destination.BindAsync(
                    client,
                    VirtualHost.VirtualHostName,
                    ExchangeName,
                    binding.RoutingKey,
                    binding.MatchHeaders,
                    cancellationToken).ConfigureAwait(false);
            }

            _tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
            resourceLogger.GetLogger(Name).LogError(ex, "Failed to apply bindings for exchange '{Exchange}'.", ExchangeName);
        }
    }

    async ValueTask<RabbitMQProbeResult> IRabbitMQProvisionable.ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var exists = await client.ExchangeExistsAsync(VirtualHost.VirtualHostName, ExchangeName, cancellationToken).ConfigureAwait(false);
        return exists
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Exchange '{ExchangeName}' does not exist in virtual host '{VirtualHost.VirtualHostName}'.");
    }

    internal override Task BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, Dictionary<string, object?>? args, CancellationToken ct)
        => client.BindExchangeAsync(vhost, sourceExchange, ExchangeName, routingKey, args, ct);
}
