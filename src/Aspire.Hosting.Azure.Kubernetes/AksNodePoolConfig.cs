// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Configuration for an AKS node pool.
/// </summary>
/// <param name="Name">The name of the node pool.</param>
/// <param name="VmSize">The VM size for nodes in the pool.</param>
/// <param name="MinCount">The minimum number of nodes.</param>
/// <param name="MaxCount">The maximum number of nodes.</param>
/// <param name="Mode">The mode of the node pool.</param>
public sealed record AksNodePoolConfig(
    string Name,
    string VmSize,
    int MinCount,
    int MaxCount,
    AksNodePoolMode Mode);

/// <summary>
/// Specifies the mode of an AKS node pool.
/// </summary>
public enum AksNodePoolMode
{
    /// <summary>
    /// System node pool for hosting system pods.
    /// </summary>
    System,

    /// <summary>
    /// User node pool for hosting application workloads.
    /// </summary>
    User
}
