// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Configuration for an AKS node pool.
/// </summary>
/// <remarks>
/// Configuration remains mutable so extensions can compose changes before infrastructure generation.
/// The final values are validated when generating the AKS agent pool profile.
/// The node pool name and mode are fixed at construction to preserve pool identity.
/// </remarks>
/// <param name="Name">The name of the node pool.</param>
/// <param name="VmSize">The VM size for nodes in the pool.</param>
/// <param name="MinCount">The minimum number of nodes.</param>
/// <param name="MaxCount">The maximum number of nodes.</param>
/// <param name="Mode">The mode of the node pool.</param>
/// <param name="OsSku">The Linux OS SKU, or <c>null</c> to let AKS choose its default.</param>
[AspireDto]
public sealed record AksNodePoolConfig(
    string Name,
    string VmSize,
    int MinCount,
    int MaxCount,
    AksNodePoolMode Mode,
    string? OsSku = null)
{
    /// <summary>
    /// Gets the name of the node pool.
    /// </summary>
    public string Name { get; } = Name;

    /// <summary>
    /// Gets the mode of the node pool.
    /// </summary>
    public AksNodePoolMode Mode { get; } = Mode;

    /// <summary>
    /// Gets or sets the VM size for nodes in the pool.
    /// </summary>
    public string VmSize { get; set; } = VmSize;

    /// <summary>
    /// Gets or sets the minimum node count for autoscaling.
    /// System pools require at least 1; user pools can scale to 0.
    /// </summary>
    public int MinCount { get; set; } = MinCount;

    /// <summary>
    /// Gets or sets the maximum node count for autoscaling.
    /// Must be at least the minimum node count.
    /// </summary>
    public int MaxCount { get; set; } = MaxCount;

    /// <summary>
    /// Gets or sets the Linux OS SKU, such as <c>AzureLinux</c> or <c>Ubuntu</c>.
    /// A <c>null</c> value leaves the SKU unspecified so AKS chooses its default.
    /// </summary>
    /// <remarks>
    /// This selects the OS SKU, not a specific OS image version. Azure validates SKU availability
    /// and compatibility with the node pool.
    /// </remarks>
    public string? OsSku { get; set; } = OsSku;
}

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
