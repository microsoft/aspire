// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Annotation that stores a subnet ID reference for AKS VNet integration.
/// Unlike <c>DelegatedSubnetAnnotation</c>, this does NOT add a service delegation
/// to the subnet — AKS uses plain (non-delegated) subnets for node pools.
/// </summary>
internal sealed class AksSubnetAnnotation(BicepOutputReference subnetId) : IResourceAnnotation
{
    /// <summary>
    /// Gets the subnet ID output reference.
    /// </summary>
    public BicepOutputReference SubnetId { get; } = subnetId;
}
