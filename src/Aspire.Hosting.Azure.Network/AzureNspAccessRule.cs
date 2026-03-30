// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Network;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an access rule configuration for an Azure Network Security Perimeter.
/// </summary>
/// <remarks>
/// Access rules control how traffic flows into and out of the network security perimeter.
/// Inbound rules specify which external sources (IP ranges or subscriptions) can access
/// resources within the perimeter. Outbound rules specify which external destinations (FQDNs)
/// resources within the perimeter can communicate with.
/// </remarks>
[AspireDto]
public sealed class AzureNspAccessRule
{
    /// <summary>
    /// Gets or sets the name of the access rule. This name must be unique within the perimeter profile.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the direction of the rule.
    /// </summary>
    public required NetworkSecurityPerimeterAccessRuleDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets the list of inbound address prefixes (CIDR ranges) allowed by this rule.
    /// </summary>
    /// <remarks>
    /// Only applicable for <see cref="NetworkSecurityPerimeterAccessRuleDirection.Inbound"/> rules.
    /// </remarks>
    public List<string>? AddressPrefixes { get; set; }

    /// <summary>
    /// Gets or sets the list of subscription IDs allowed by this rule.
    /// </summary>
    /// <remarks>
    /// Only applicable for <see cref="NetworkSecurityPerimeterAccessRuleDirection.Inbound"/> rules.
    /// Subscription IDs should be in the format of a resource ID: <c>/subscriptions/{subscriptionId}</c>.
    /// </remarks>
    public List<string>? Subscriptions { get; set; }

    /// <summary>
    /// Gets or sets the list of fully qualified domain names (FQDNs) allowed by this rule.
    /// </summary>
    /// <remarks>
    /// Only applicable for <see cref="NetworkSecurityPerimeterAccessRuleDirection.Outbound"/> rules.
    /// </remarks>
    public List<string>? FullyQualifiedDomainNames { get; set; }
}
