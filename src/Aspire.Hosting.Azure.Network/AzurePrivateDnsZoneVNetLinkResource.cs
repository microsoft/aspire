// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Private DNS Zone VNet Link resource.
/// </summary>
internal sealed class AzurePrivateDnsZoneVNetLinkResource(
    string name,
    AzurePrivateDnsZoneResource dnsZone,
    AzureVirtualNetworkResource vnet) : Resource(name), IResourceWithParent<AzurePrivateDnsZoneResource>
{
    /// <summary>
    /// Gets the parent DNS Zone resource.
    /// </summary>
    public AzurePrivateDnsZoneResource Parent { get; } = dnsZone;

    /// <summary>
    /// Gets the VNet resource linked to the DNS Zone.
    /// </summary>
    public AzureVirtualNetworkResource VNet { get; } = vnet;
}
