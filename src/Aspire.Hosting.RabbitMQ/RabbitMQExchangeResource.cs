// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ exchange resource that is declared on the broker during provisioning.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, ExchangeName = {ExchangeName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQExchangeResource : RabbitMQDestination, IResourceWithConnectionString, IResourceWithExchangeArguments
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

    /// <summary>Gets the name of the exchange.</summary>
    public string ExchangeName { get; }

    /// <summary>Gets the routing algorithm used by this exchange. Set via the <c>type</c> parameter of <c>AddExchange</c>.</summary>
    public RabbitMQExchangeType ExchangeType { get; }

    /// <summary>Gets or sets a value indicating whether the exchange is durable.</summary>
    public bool Durable { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the exchange is auto-deleted.</summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    /// Gets the exchange arguments for this exchange declaration, such as the alternate exchange for unroutable messages.
    /// </summary>
    /// <remarks>
    /// Use <see cref="RabbitMQExchangeExtensions.WithExchangeArguments{T}"/> to configure these settings.
    /// </remarks>
    public RabbitMQExchangeArguments ExchangeArguments { get; } = new();

    internal List<RabbitMQBinding> Bindings { get; } = [];

    /// <summary>Gets the policies that apply to this exchange, resolved at startup from matching <c>AddPolicy</c> calls on the parent virtual host.</summary>
    internal List<RabbitMQPolicyResource> AppliedPolicies { get; } = [];

    internal override IEnumerable<RabbitMQProvisionableResource> HealthDependencies
        // The alternate exchange must be provisioned before this exchange's health is meaningful.
        => ExchangeArguments.AlternateExchange is { } ae
            ? [.. AppliedPolicies, ae]
            : [.. AppliedPolicies];

    /// <inheritdoc/>
    public override string ProvisionedName => ExchangeName;

    /// <inheritdoc/>
    public override RabbitMQDestinationKind Kind => RabbitMQDestinationKind.Exchange;

    /// <summary>Gets the connection string properties for this exchange, including the exchange name.</summary>
    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        VirtualHost.CombineProperties([
            new("ExchangeName", ReferenceExpression.Create($"{ExchangeName}")),
        ]);

    /// <summary>
    /// Builds the declared exchange arguments (the x-arguments Aspire authors) for this exchange.
    /// Shared by <see cref="ReconcileAsync"/> (declare) and <see cref="ProbeAsync"/> (drift comparison)
    /// so both sides compare the exact same bag.
    /// </summary>
    private Dictionary<string, object?> BuildDeclaredArguments()
    {
        var args = new Dictionary<string, object?>();
        ExchangeArguments.FlattenInto(args, $"Exchange '{ExchangeName}'");
        return args;
    }

    private string ExchangeTypeString => ExchangeType.ToString().ToLowerInvariant();

    internal override async ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var args = BuildDeclaredArguments();

        await client.DeclareExchangeAsync(
            VirtualHost.VirtualHostName,
            ExchangeName,
            ExchangeTypeString,
            Durable,
            AutoDelete,
            args.Count > 0 ? args : null,
            cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => await client.DeleteExchangeAsync(VirtualHost.VirtualHostName, ExchangeName, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var live = await client.GetExchangeAsync(VirtualHost.VirtualHostName, ExchangeName, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return RabbitMQProbeResult.Unhealthy($"Exchange '{ExchangeName}' does not exist in virtual host '{VirtualHost.VirtualHostName}'.");
        }

        // Compare only declared fields; server-computed fields (e.g. effective_policy_definition) are not mapped.
        var checker = new RabbitMQDriftChecker("Exchange", ExchangeName);

        // Skip type comparison when the broker reports empty — older versions omit it.
        if (!string.IsNullOrEmpty(live.Type))
        {
            checker.Field("type", ExchangeTypeString, live.Type);
        }

        return checker
            .Field("durable", Durable, live.Durable)
            .Field("auto-delete", AutoDelete, live.AutoDelete)
            .Arguments($"Exchange '{ExchangeName}'", BuildDeclaredArguments(), live.Arguments)
            .Result;
    }

    internal override Task BindAsync(IRabbitMQProvisioningClient client, string vhost, string sourceExchange, string routingKey, Dictionary<string, object?>? args, CancellationToken ct)
        => client.BindExchangeAsync(vhost, sourceExchange, ExchangeName, routingKey, args, ct);
}
