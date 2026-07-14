// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Network profile configuration for an AKS cluster.
/// </summary>
internal sealed class AksNetworkProfile
{
    /// <summary>
    /// Gets or sets the network plugin. Defaults to "azure" for Azure CNI.
    /// </summary>
    public string NetworkPlugin { get; set; } = "azure";

    /// <summary>
    /// Gets or sets the network policy. Defaults to "calico".
    /// </summary>
    public string? NetworkPolicy { get; set; } = "calico";

    /// <summary>
    /// Gets or sets the service CIDR.
    /// </summary>
    public string ServiceCidr { get; set; } = "10.0.4.0/22";

    /// <summary>
    /// Gets or sets the DNS service IP address.
    /// </summary>
    public string DnsServiceIP { get; set; } = "10.0.4.10";
}
