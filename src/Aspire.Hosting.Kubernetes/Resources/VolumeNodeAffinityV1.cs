// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the volume node affinity configuration in Kubernetes.
/// VolumeNodeAffinityV1 describes the node-specific constraints for a PersistentVolume
/// to ensure storage resources are bound to specific nodes based on the required
/// scheduling and node affinity rules.
/// </summary>
[YamlSerializable]
public sealed class VolumeNodeAffinityV1
{
    /// <summary>
    /// Defines the required node affinity constraints for scheduling a Kubernetes volume.
    /// </summary>
    /// <remarks>
    /// This property specifies mandatory node selection criteria using a <see cref="NodeSelectorV1"/> object.
    /// The criteria are used to determine the nodes on which a Kubernetes volume can be scheduled.
    /// It enables the definition of strict scheduling constraints that must be met for a node to be eligible.
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML
    /// when no required selector has been configured. Eagerly initializing this to
    /// <c>new()</c> would otherwise emit an invalid empty <c>required: {}</c> mapping that
    /// Kubernetes rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "required")]
    public NodeSelectorV1? Required { get; set; }
}
