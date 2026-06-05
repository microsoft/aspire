// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the rolling update strategy configuration for a StatefulSet in Kubernetes.
/// </summary>
/// <remarks>
/// This class specifies parameters for controlling the rolling update process of a StatefulSet, allowing updates of its Pods while maintaining certain constraints.
/// </remarks>
[YamlSerializable]
public sealed class RollingUpdateStatefulSetStrategyV1
{
    /// <summary>
    /// Gets or sets the maximum number of unavailable pods permitted during the rolling update of a StatefulSet.
    /// This property defines either an absolute number or a percentage of pods that can be unavailable simultaneously
    /// while the StatefulSet is being updated to achieve its desired state.
    /// </summary>
    [YamlMember(Alias = "maxUnavailable")]
    public int? MaxUnavailable { get; set; }

    /// <summary>
    /// Gets or sets the ordinal at which the StatefulSet should be partitioned.
    /// Pods with an ordinal greater than or equal to the specified partition value will be updated when the StatefulSet's Pod template is updated.
    /// Pods with an ordinal less than the partition value remain unchanged.
    /// This property enables management of updates to subsets of the StatefulSet.
    /// </summary>
    [YamlMember(Alias = "partition")]
    public int? Partition { get; set; }
}
