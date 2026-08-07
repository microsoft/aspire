// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.Hosting.RabbitMQ.Provisioning;

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
public class RabbitMQPolicyResource : RabbitMQProvisionableResource, IResourceWithQueueArguments, IResourceWithExchangeArguments, IRabbitMQServerChild
{
    private readonly Regex _compiledPattern;

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

        try
        {
            _compiledPattern = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException(
                $"Invalid regex pattern '{pattern}': {ex.Message}", nameof(pattern), ex);
        }
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

        return _compiledPattern.IsMatch(entityName);
    }

    private RabbitMQPolicyDefinition BuildDefinition()
    {
        var definition = new Dictionary<string, object?>();

        if (ApplyTo != RabbitMQPolicyApplyTo.Exchanges)
        {
            QueueArguments.FlattenIntoPolicy(definition, $"Policy '{PolicyName}'");
        }

        if (ApplyTo != RabbitMQPolicyApplyTo.Queues)
        {
            ExchangeArguments.FlattenInto(definition, $"Policy '{PolicyName}'");
        }

        foreach (var (k, v) in AdditionalArguments)
        {
            definition[k] = v;
        }

        return new RabbitMQPolicyDefinition(
            Pattern,
            ApplyTo.ToString().ToLowerInvariant(),
            definition,
            Priority);
    }

    internal override async ValueTask ReconcileAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => await client.PutPolicyAsync(Parent.VirtualHostName, PolicyName, BuildDefinition(), cancellationToken).ConfigureAwait(false);

    internal override async ValueTask DeleteAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => await client.DeletePolicyAsync(Parent.VirtualHostName, PolicyName, cancellationToken).ConfigureAwait(false);

    internal override async ValueTask<RabbitMQProbeResult> ProbeAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var live = await client.GetPolicyAsync(Parent.VirtualHostName, PolicyName, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return RabbitMQProbeResult.Unhealthy($"Policy '{PolicyName}' does not exist in virtual host '{Parent.VirtualHostName}'.");
        }

        // Drift is scoped to the policy object itself — pattern matching is the server's business.
        var desired = BuildDefinition();

        return new RabbitMQDriftChecker("Policy", PolicyName)
            .Field("pattern", desired.Pattern, live.Pattern, StringComparison.Ordinal)
            .Field("apply-to", desired.ApplyTo, live.ApplyTo)
            .Field("priority", desired.Priority, live.Priority)
            .Arguments($"Policy '{PolicyName}' definition", desired.Definition, live.Definition)
            .Result;
    }

    RabbitMQVirtualHostResource IRabbitMQServerChild.VirtualHost => Parent;
}
