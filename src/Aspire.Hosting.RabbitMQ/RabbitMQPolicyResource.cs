// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a RabbitMQ policy.
/// </summary>
/// <remarks>
/// Policies are applied to queues and/or exchanges whose names match the <see cref="Pattern"/> regex.
/// They configure runtime behaviour such as message TTL, dead-letter routing, and queue length limits.
/// A policy does not expose a connection string — it is a configuration artifact that affects the
/// entities it matches. The health of a matching queue or exchange includes waiting for this policy
/// to be applied successfully.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, PolicyName = {PolicyName}")]
[AspireExport(ExposeProperties = true)]
public class RabbitMQPolicyResource : Resource, IResourceWithParent<RabbitMQVirtualHostResource>, IRabbitMQProvisionable
{
    private Regex? _compiledPattern;

    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQPolicyResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="policyName">The name of the policy in RabbitMQ.</param>
    /// <param name="pattern">The regex pattern that determines which queues and/or exchanges the policy applies to.</param>
    /// <param name="parent">The RabbitMQ virtual host resource associated with this policy.</param>
    public RabbitMQPolicyResource(string name, string policyName, string pattern, RabbitMQVirtualHostResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(parent);

        PolicyName = policyName;
        Pattern = pattern;
        Parent = parent;
    }

    /// <summary>
    /// Gets the name of the policy in RabbitMQ.
    /// </summary>
    public string PolicyName { get; }

    /// <summary>
    /// Gets the regex pattern that determines which queues and/or exchanges the policy applies to.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets the parent RabbitMQ virtual host resource.
    /// </summary>
    public RabbitMQVirtualHostResource Parent { get; }

    /// <summary>
    /// Gets or sets which entity types the policy applies to.
    /// </summary>
    public RabbitMQPolicyApplyTo ApplyTo { get; set; } = RabbitMQPolicyApplyTo.All;

    /// <summary>
    /// Gets the policy definition key-value pairs (e.g. <c>message-ttl</c>, <c>dead-letter-exchange</c>).
    /// </summary>
    public IDictionary<string, object?> Definition { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or sets the policy priority. Higher values take precedence when multiple policies match.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Returns <see langword="true"/> if this policy applies to the entity with the given name and kind.
    /// </summary>
    /// <param name="entityName">The wire name of the queue or exchange.</param>
    /// <param name="kind">Whether the entity is a queue or exchange.</param>
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

    /// <summary>
    /// Completed when this policy has been applied to the broker.
    /// Faulted if the PUT request failed.
    /// </summary>
    internal TaskCompletionSource ProvisioningComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    TaskCompletionSource IRabbitMQProvisionable.ProvisioningComplete => ProvisioningComplete;

    Task IRabbitMQProvisionable.ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
        => ApplyAsync(client, cancellationToken);

    internal Task ApplyAsync(IRabbitMQProvisioningClient client, CancellationToken cancellationToken)
    {
        var def = new RabbitMQPolicyDefinition(
            Pattern,
            ApplyTo.ToString().ToLowerInvariant(),
            Definition,
            Priority);

        return client.PutPolicyAsync(Parent.VirtualHostName, PolicyName, def, cancellationToken);
    }
}
