// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a RabbitMQ policy resource that is applied to matching queues and/or exchanges during provisioning.
/// </summary>
/// <remarks>
/// Policies are applied to queues and/or exchanges whose names match the <see cref="Pattern"/> regex
/// and configure runtime behaviour such as message TTL, dead-letter routing, and queue length limits.
/// Setting <see cref="QueueArguments"/> on a policy whose <see cref="ApplyTo"/> is
/// <see cref="RabbitMQPolicyApplyTo.Exchanges"/>, or <see cref="ExchangeArguments"/> on a policy
/// whose <see cref="ApplyTo"/> is <see cref="RabbitMQPolicyApplyTo.Queues"/>, will throw at startup.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, PolicyName = {PolicyName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQPolicyResource : Resource, IResourceWithParent<RabbitMQVirtualHostResource>, IRabbitMQProvisionable, IResourceWithQueueArguments, IResourceWithExchangeArguments, IRabbitMQServerChild
{
    private Regex? _compiledPattern;

    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQPolicyResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="policyName">The name of the policy in RabbitMQ.</param>
    /// <param name="pattern">The regex pattern that determines which queues and/or exchanges the policy applies to.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this policy.</param>
    /// <param name="applyTo">Which entity types the policy applies to.</param>
    /// <param name="priority">The policy priority. Higher values take precedence when multiple policies match.</param>
    public RabbitMQPolicyResource(string name, string policyName, string pattern, RabbitMQVirtualHostResource parent,
        RabbitMQPolicyApplyTo applyTo = RabbitMQPolicyApplyTo.All, int priority = 0) : base(name)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(parent);

        PolicyName = policyName;
        Pattern = pattern;
        Parent = parent;
        ApplyTo = applyTo;
        Priority = priority;
    }

    /// <summary>
    /// Gets the name of the policy as known to the broker.
    /// </summary>
    public string PolicyName { get; }

    /// <summary>
    /// Gets the regex pattern that determines which queues and/or exchanges this policy applies to.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets the virtual host in which this policy is defined.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

    /// <summary>
    /// Gets which entity types (queues, exchanges, or both) this policy applies to.
    /// </summary>
    public RabbitMQPolicyApplyTo ApplyTo { get; }

    /// <summary>
    /// Gets the policy priority. Higher values take precedence when multiple policies match the same entity.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Gets the queue arguments applied by this policy to matching queues.
    /// </summary>
    /// <remarks>
    /// Only used when <see cref="ApplyTo"/> is <see cref="RabbitMQPolicyApplyTo.Queues"/> or <see cref="RabbitMQPolicyApplyTo.All"/>.
    /// </remarks>
    public RabbitMQQueueArguments QueueArguments { get; } = new();

    /// <summary>
    /// Gets the exchange arguments applied by this policy to matching exchanges.
    /// </summary>
    /// <remarks>
    /// Only used when <see cref="ApplyTo"/> is <see cref="RabbitMQPolicyApplyTo.Exchanges"/> or <see cref="RabbitMQPolicyApplyTo.All"/>.
    /// </remarks>
    public RabbitMQExchangeArguments ExchangeArguments { get; } = new();

    /// <summary>
    /// Gets additional policy definition keys not covered by <see cref="QueueArguments"/> or <see cref="ExchangeArguments"/>,
    /// such as <c>ha-mode</c>, <c>federation-upstream</c>, or <c>ha-sync-mode</c>.
    /// </summary>
    /// <remarks>
    /// Entries may be added until the application starts. Mutations after <see cref="BeforeStartEvent"/> are ignored.
    /// </remarks>
    public Dictionary<string, object?> AdditionalArguments { get; } = [];

    /// <summary>
    /// Returns <see langword="true"/> if this policy applies to the entity with the given name and kind.
    /// </summary>
    /// <param name="entityName">The broker wire name of the queue or exchange.</param>
    /// <param name="kind">The kind of the entity (queue or exchange).</param>
    internal bool AppliesTo(string entityName, RabbitMQDestinationKind kind)
    {
        var scopeMatches = ApplyTo switch
        {
            RabbitMQPolicyApplyTo.Queues => kind == RabbitMQDestinationKind.Queue,
            RabbitMQPolicyApplyTo.Exchanges => kind == RabbitMQDestinationKind.Exchange,
            RabbitMQPolicyApplyTo.All => true,
            _ => false,
        };

        if (!scopeMatches)
        {
            return false;
        }

        _compiledPattern ??= new Regex(Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        return _compiledPattern.IsMatch(entityName);
    }

    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Task IRabbitMQProvisionable.ProvisionedTask => _tcs.Task;

    async Task IRabbitMQProvisionable.ApplyAsync(IRabbitMQProvisioningClient client, ResourceNotificationService notifications, ResourceLoggerService resourceLogger, CancellationToken cancellationToken)
    {
        await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Starting }).ConfigureAwait(false);
        try
        {
            if (ApplyTo == RabbitMQPolicyApplyTo.Exchanges &&
                (QueueArguments.MessageTtl is not null || QueueArguments.MaxLength is not null ||
                 QueueArguments.MaxLengthBytes is not null || QueueArguments.Expires is not null ||
                 QueueArguments.DeadLetterExchange is not null || QueueArguments.DeadLetterRoutingKey is not null ||
                 QueueArguments.AdditionalArguments.Count > 0))
            {
                throw new DistributedApplicationException(
                    $"Policy '{PolicyName}' has QueueArguments set but ApplyTo is '{nameof(RabbitMQPolicyApplyTo.Exchanges)}'. " +
                    $"Queue arguments are ignored when a policy only targets exchanges. " +
                    $"Set ApplyTo to '{nameof(RabbitMQPolicyApplyTo.Queues)}' or '{nameof(RabbitMQPolicyApplyTo.All)}', or clear QueueArguments.");
            }

            if (ApplyTo == RabbitMQPolicyApplyTo.Queues &&
                (ExchangeArguments.AlternateExchange is not null || ExchangeArguments.AdditionalArguments.Count > 0))
            {
                throw new DistributedApplicationException(
                    $"Policy '{PolicyName}' has ExchangeArguments set but ApplyTo is '{nameof(RabbitMQPolicyApplyTo.Queues)}'. " +
                    $"Exchange arguments are ignored when a policy only targets queues. " +
                    $"Set ApplyTo to '{nameof(RabbitMQPolicyApplyTo.Exchanges)}' or '{nameof(RabbitMQPolicyApplyTo.All)}', or clear ExchangeArguments.");
            }

            var definition = new Dictionary<string, object?>();
            QueueArguments.FlattenInto(definition, $"Policy '{PolicyName}'");
            ExchangeArguments.FlattenInto(definition, $"Policy '{PolicyName}'");

            foreach (var (k, v) in AdditionalArguments)
            {
                definition[k] = v;
            }

            var def = new RabbitMQPolicyDefinition(
                Pattern,
                ApplyTo.ToString().ToLowerInvariant(),
                definition,
                Priority);

            await client.PutPolicyAsync(Parent.VirtualHostName, PolicyName, def, cancellationToken).ConfigureAwait(false);

            _tcs.TrySetResult();
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.Running }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
            resourceLogger.GetLogger(Name).LogError(ex, "Failed to apply policy '{Policy}'.", PolicyName);
            await notifications.PublishUpdateAsync(this, s => s with { State = KnownResourceStates.FailedToStart }).ConfigureAwait(false);
        }
    }

    async ValueTask<RabbitMQProbeResult> IRabbitMQProvisionable.ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var exists = await client.PolicyExistsAsync(Parent.VirtualHostName, PolicyName, cancellationToken).ConfigureAwait(false);
        return exists
            ? RabbitMQProbeResult.Healthy
            : RabbitMQProbeResult.Unhealthy($"Policy '{PolicyName}' does not exist in virtual host '{Parent.VirtualHostName}'.");
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => Parent;
}
