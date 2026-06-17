// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Annotation that associates a compute resource with a specific Kubernetes node pool.
/// When present, the Kubernetes deployment will include a <c>nodeSelector</c> targeting
/// the specified node pool.
/// </summary>
internal sealed class KubernetesNodePoolAnnotation(KubernetesNodePoolResource nodePool) : IResourceAnnotation
{
    /// <summary>
    /// Gets the node pool to schedule the workload on.
    /// </summary>
    public KubernetesNodePoolResource NodePool { get; } = nodePool;
}
