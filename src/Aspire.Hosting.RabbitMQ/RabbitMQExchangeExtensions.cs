// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for adding RabbitMQ exchange resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQExchangeExtensions
{
    /// <summary>
    /// Adds an exchange to a RabbitMQ virtual host.
    /// </summary>
    /// <param name="builder">The RabbitMQ virtual host resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="exchangeName">The name of the exchange. Defaults to the resource name when not provided.</param>
    /// <param name="type">The type of the exchange. Defaults to <see cref="RabbitMQExchangeType.Direct"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds an exchange to a RabbitMQ virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQExchangeResource> AddExchange(
        this IResourceBuilder<RabbitMQVirtualHostResource> builder,
        [ResourceName] string name,
        string? exchangeName = null,
        RabbitMQExchangeType type = RabbitMQExchangeType.Direct)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (exchangeName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(exchangeName, nameof(exchangeName));
        }

        var exName = exchangeName ?? name;
        if (builder.Resource.Exchanges.Any(e => e.ExchangeName == exName))
        {
            throw new DistributedApplicationException($"An exchange with the name '{exName}' already exists in virtual host '{builder.Resource.VirtualHostName}'.");
        }

        var exchange = new RabbitMQExchangeResource(name, exName, builder.Resource, type);

        builder.Resource.Exchanges.Add(exchange);

        return builder.ApplicationBuilder.AddResource(exchange)
            .WithIconName("ArrowSwap")
            .WithRabbitMQTopologyWiring(builder.Resource)
            .WithExchangeBindings();
    }

    /// <summary>
    /// Adds an exchange to the default <c>/</c> virtual host of a RabbitMQ server.
    /// </summary>
    /// <param name="builder">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="exchangeName">The name of the exchange. Defaults to the resource name when not provided.</param>
    /// <param name="type">The type of the exchange. Defaults to <see cref="RabbitMQExchangeType.Direct"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds an exchange to the default '/' virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("addExchangeOnServer", MethodName = "addExchange")]
    public static IResourceBuilder<RabbitMQExchangeResource> AddExchange(
        this IResourceBuilder<RabbitMQServerResource> builder,
        [ResourceName] string name,
        string? exchangeName = null,
        RabbitMQExchangeType type = RabbitMQExchangeType.Direct)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return RabbitMQBuilderExtensions.GetOrAddDefaultVirtualHost(builder).AddExchange(name, exchangeName, type);
    }

    /// <summary>
    /// Adds a binding from an exchange to a destination.
    /// </summary>
    /// <typeparam name="TDestination">The type of the destination resource.</typeparam>
    /// <param name="exchange">The exchange resource builder.</param>
    /// <param name="destination">The destination resource builder.</param>
    /// <param name="routingKey">The routing key for the binding.</param>
    /// <param name="matchHeaders">
    /// The headers-exchange match arguments for the binding.
    /// Used when the source exchange is of type <see cref="RabbitMQExchangeType.Headers"/> to specify
    /// which message headers must match for the binding to be selected.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a binding from an exchange to a queue or another exchange.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQExchangeResource> WithBinding<TDestination>(
        this IResourceBuilder<RabbitMQExchangeResource> exchange,
        IResourceBuilder<TDestination> destination,
        string routingKey = "",
        Dictionary<string, object?>? matchHeaders = null)
        where TDestination : RabbitMQDestination
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (exchange.Resource.VirtualHost != destination.Resource.VirtualHost)
        {
            throw new DistributedApplicationException($"Cannot bind exchange '{exchange.Resource.Name}' to destination '{destination.Resource.Name}' because they are in different virtual hosts.");
        }

        var binding = new RabbitMQBinding(destination.Resource, routingKey, matchHeaders);
        exchange.Resource.Bindings.Add(binding);

        return exchange.WithRelationship(destination.Resource, "Binding");
    }

    /// <summary>
    /// Configures exchange settings such as the alternate exchange for unroutable messages.
    /// </summary>
    /// <typeparam name="T">
    /// The resource type. Accepts <see cref="RabbitMQExchangeResource"/> (to set per-exchange arguments)
    /// and <see cref="RabbitMQPolicyResource"/> (to apply the same settings via a broker policy).
    /// </typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">An action that sets properties on the <see cref="RabbitMQExchangeArguments"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// Route unroutable messages to a dedicated exchange via a policy:
    /// <code>
    /// vhost.AddPolicy("ae-policy", ".*", RabbitMQPolicyApplyTo.Exchanges)
    ///      .WithExchangeArguments(a => a.AlternateExchange = unroutable.Resource);
    /// </code>
    /// </example>
    /// <ats-summary>Configures typed exchange x-arguments.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithExchangeArguments<T>(
        this IResourceBuilder<T> builder,
        Action<RabbitMQExchangeArguments> configure)
        where T : IResourceWithExchangeArguments
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Resource.ExchangeArguments);
        return builder;
    }

    /// <summary>
    /// Routes messages that cannot be delivered by this exchange to the specified alternate exchange.
    /// </summary>
    /// <typeparam name="T">The resource type. Accepts <see cref="RabbitMQExchangeResource"/> and <see cref="RabbitMQPolicyResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="alternateExchange">The exchange that will receive unroutable messages.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="DistributedApplicationException">
    /// Thrown when <paramref name="alternateExchange"/> is in a different virtual host than the exchange.
    /// </exception>
    /// <example>
    /// Capture unroutable messages in a dedicated exchange:
    /// <code>
    /// var unroutable = vhost.AddExchange("unroutable");
    ///
    /// vhost.AddExchange("orders")
    ///      .WithAlternateExchange(unroutable);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Generic constraint uses IRabbitMQServerChild which is not ATS-compatible. Use WithExchangeArguments to set AlternateExchange directly in polyglot app hosts.")]
    public static IResourceBuilder<T> WithAlternateExchange<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RabbitMQExchangeResource> alternateExchange)
        where T : Resource, IResourceWithExchangeArguments, IRabbitMQServerChild
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(alternateExchange);

        // The topology types no longer implement IResourceWithParent; use IRabbitMQServerChild.VirtualHost
        // to reach the owning virtual host for the same-vhost validation.
        if (alternateExchange.Resource.VirtualHost != ((IRabbitMQServerChild)builder.Resource).VirtualHost)
        {
            throw new DistributedApplicationException(
                $"Alternate exchange '{alternateExchange.Resource.Name}' must be in the same virtual host as '{builder.Resource.Name}'.");
        }

        builder.Resource.ExchangeArguments.SetAlternateExchange(alternateExchange.Resource);
        return builder.WithRelationship(alternateExchange.Resource, "AlternateExchange");
    }

    /// <summary>
    /// Configures properties of a RabbitMQ exchange.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withExchangeProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQExchangeResource> WithProperties(this IResourceBuilder<RabbitMQExchangeResource> builder, Action<RabbitMQExchangeResource> configure)
        => RabbitMQBuilderExtensions.WithPropertiesCore(builder, configure);
}
