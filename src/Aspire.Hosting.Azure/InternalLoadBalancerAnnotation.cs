// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an internal load balancer whose private DNS zone is linked to a virtual network.
/// </summary>
/// <param name="virtualNetwork">The virtual network containing the resource's delegated subnet.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class InternalLoadBalancerAnnotation(IResource virtualNetwork) : IResourceAnnotation
{
    /// <summary>
    /// Gets the virtual network linked to the private DNS zone.
    /// </summary>
    public IResource VirtualNetwork { get; } = virtualNetwork;
}
