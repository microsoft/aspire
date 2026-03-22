// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.Network;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a security rule configuration for an Azure Network Security Group.
/// </summary>
/// <remarks>
/// Security rules control inbound and outbound network traffic for subnets associated with the Network Security Group.
/// Rules are evaluated in priority order, with lower numbers having higher priority.
/// </remarks>
[AspireDto]
public sealed class AzureSecurityRule
{
    /// <summary>
    /// Gets or sets the name of the security rule. This name must be unique within the Network Security Group.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets an optional description for the security rule.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the priority of the rule. Valid values are between 100 and 4096. Lower numbers have higher priority.
    /// </summary>
    public required int Priority { get; set; }

    /// <summary>
    /// Gets or sets the direction of the rule.
    /// </summary>
    public required SecurityRuleDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets whether network traffic is allowed or denied.
    /// </summary>
    public required SecurityRuleAccess Access { get; set; }

    /// <summary>
    /// Gets or sets the network protocol this rule applies to.
    /// </summary>
    public required SecurityRuleProtocol Protocol { get; set; }

    /// <summary>
    /// Gets or sets the source address prefix. Defaults to "*" (any).
    /// </summary>
    public string SourceAddressPrefix { get; set; } = "*";

    /// <summary>
    /// Gets or sets a <see cref="ReferenceExpression"/> whose value is resolved at deploy time
    /// and used as the source address prefix for the rule. Takes precedence over
    /// <see cref="SourceAddressPrefix"/>.
    /// </summary>
    public ReferenceExpression? SourceAddressPrefixReference { get; set; }

    /// <summary>
    /// Gets or sets the source port range. Defaults to "*" (any).
    /// </summary>
    public string SourcePortRange { get; set; } = "*";

    /// <summary>
    /// Gets or sets the destination address prefix. Defaults to "*" (any).
    /// </summary>
    public string DestinationAddressPrefix { get; set; } = "*";

    /// <summary>
    /// Gets or sets a <see cref="ReferenceExpression"/> whose value is resolved at deploy time
    /// and used as the destination address prefix for the rule. Takes precedence over
    /// <see cref="DestinationAddressPrefix"/>.
    /// </summary>
    public ReferenceExpression? DestinationAddressPrefixReference { get; set; }

    /// <summary>
    /// Gets or sets the destination port range. Use "*" for any, or a range like "80-443".
    /// </summary>
    public required string DestinationPortRange { get; set; }
}
