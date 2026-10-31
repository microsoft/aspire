// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure resource that supports an internal load balancer and a private DNS zone.
/// </summary>
/// <remarks>
/// Implementations apply <see cref="InternalLoadBalancerAnnotation"/> when provisioning and expose the
/// resulting domain and IP address so the network integration can configure private DNS.
/// The domain and static IP references are intended for internal environments configured with that annotation,
/// not for unsupported configurations such as Azure Container Apps Express.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public interface IAzureInternalIngressResource : IAzureDelegatedSubnetResource
{
    /// <summary>
    /// Gets the default domain used as the private DNS zone name.
    /// </summary>
    ReferenceExpression DefaultDomain { get; }

    /// <summary>
    /// Gets the internal load balancer's static IP address.
    /// </summary>
    /// <remarks>
    /// Valid only when the resource is configured with <see cref="InternalLoadBalancerAnnotation"/>
    /// and supports internal networking. The backing output need not exist for other configurations.
    /// </remarks>
    ReferenceExpression StaticIp { get; }
}
