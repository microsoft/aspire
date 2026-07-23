// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides extension methods for adding RabbitMQ policy resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RabbitMQPolicyExtensions
{
    /// <summary>
    /// Adds a policy to a RabbitMQ virtual host.
    /// </summary>
    /// <remarks>
    /// Policies are applied to queues and/or exchanges whose names match <paramref name="pattern"/> (a regex)
    /// and configure runtime behaviour such as message TTL, dead-letter routing, and queue length limits.
    /// Policies require the management plugin, which is enabled automatically when a non-default virtual host is added.
    /// </remarks>
    /// <param name="builder">The RabbitMQ virtual host resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="pattern">The regex pattern that determines which queues and/or exchanges the policy applies to.</param>
    /// <param name="applyTo">Which entity types the policy applies to. Defaults to <see cref="RabbitMQPolicyApplyTo.All"/>.</param>
    /// <param name="priority">The policy priority. Higher values take precedence when multiple policies match the same entity. Defaults to <c>0</c>.</param>
    /// <param name="policyName">The name of the policy in RabbitMQ. Defaults to the resource name when not provided.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a policy to a RabbitMQ virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<RabbitMQPolicyResource> AddPolicy(
        this IResourceBuilder<RabbitMQVirtualHostResource> builder,
        [ResourceName] string name,
        string pattern,
        RabbitMQPolicyApplyTo applyTo = RabbitMQPolicyApplyTo.All,
        int priority = 0,
        string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        if (policyName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyName, nameof(policyName));
        }

        var wireName = policyName ?? name;
        if (builder.Resource.Policies.Any(p => p.PolicyName == wireName))
        {
            throw new DistributedApplicationException($"A policy with the name '{wireName}' already exists in virtual host '{builder.Resource.VirtualHostName}'.");
        }

        var policy = new RabbitMQPolicyResource(name, wireName, pattern, builder.Resource, applyTo, priority);
        builder.Resource.Policies.Add(policy);

        var policyBuilder = builder.ApplicationBuilder.AddResource(policy);

        // Resolve matches at model-freeze time (BeforeStartEvent) so entities added after the policy are included.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, ct) =>
        {
            RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policy, builder.Resource, policyBuilder);
            return Task.CompletedTask;
        });

        return policyBuilder
            .WithIconName("ClipboardSettings")
            .WithRabbitMQTopologyWiring(builder.Resource);
    }

    /// <summary>
    /// Adds a policy to the default <c>/</c> virtual host of a RabbitMQ server.
    /// </summary>
    /// <param name="server">The RabbitMQ server resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="pattern">The regex pattern that determines which queues and/or exchanges the policy applies to.</param>
    /// <param name="applyTo">Which entity types the policy applies to. Defaults to <see cref="RabbitMQPolicyApplyTo.All"/>.</param>
    /// <param name="priority">The policy priority. Higher values take precedence when multiple policies match the same entity. Defaults to <c>0</c>.</param>
    /// <param name="policyName">The name of the policy in RabbitMQ. Defaults to the resource name when not provided.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-summary>Adds a policy to the default '/' virtual host.</ats-summary>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("addPolicyOnServer", MethodName = "addPolicy")]
    public static IResourceBuilder<RabbitMQPolicyResource> AddPolicy(
        this IResourceBuilder<RabbitMQServerResource> server,
        [ResourceName] string name,
        string pattern,
        RabbitMQPolicyApplyTo applyTo = RabbitMQPolicyApplyTo.All,
        int priority = 0,
        string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        return RabbitMQBuilderExtensions.GetOrAddDefaultVirtualHost(server).AddPolicy(name, pattern, applyTo, priority, policyName);
    }

    /// <summary>
    /// Configures additional policy settings such as <see cref="RabbitMQPolicyResource.AdditionalArguments"/>.
    /// To configure typed queue or exchange arguments, use <see cref="RabbitMQQueueExtensions.WithQueueArguments{T}"/> or <see cref="RabbitMQExchangeExtensions.WithExchangeArguments{T}"/> instead.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withPolicyProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RabbitMQPolicyResource> WithProperties(this IResourceBuilder<RabbitMQPolicyResource> builder, Action<RabbitMQPolicyResource> configure)
        => RabbitMQBuilderExtensions.WithPropertiesCore(builder, configure);
}
