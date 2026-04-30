// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes node pool as a child resource of a <see cref="KubernetesEnvironmentResource"/>.
/// Node pools can be referenced by compute resources to schedule workloads on specific node pools
/// using <see cref="KubernetesEnvironmentExtensions.WithNodePool{T}"/>.
/// </summary>
/// <param name="name">The name of the node pool resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
[AspireExport]
public class KubernetesNodePoolResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Gets the label key used to identify the node pool in the Kubernetes cluster.
    /// Defaults to <c>agentpool</c> which is the standard label used by AKS and many managed Kubernetes services.
    /// </summary>
    public string NodeSelectorLabelKey { get; init; } = "agentpool";
}
