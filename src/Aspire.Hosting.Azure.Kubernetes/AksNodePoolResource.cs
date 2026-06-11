// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Represents an AKS node pool with Azure-specific configuration such as VM size and autoscaling.
/// Extends the base <see cref="KubernetesNodePoolResource"/> with provisioning configuration
/// that is used to generate Azure Bicep for the AKS agent pool profile.
/// </summary>
/// <param name="name">The name of the node pool resource.</param>
/// <param name="config">The Azure-specific node pool configuration.</param>
/// <param name="parent">The parent AKS environment resource.</param>
public class AksNodePoolResource(
    string name,
    AksNodePoolConfig config,
    AzureKubernetesEnvironmentResource parent) : KubernetesNodePoolResource(name, parent.KubernetesEnvironment)
{
    /// <summary>
    /// Gets the parent AKS environment resource.
    /// </summary>
    public AzureKubernetesEnvironmentResource AksParent { get; } = parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Gets the Azure-specific node pool configuration.
    /// </summary>
    public AksNodePoolConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));
}
