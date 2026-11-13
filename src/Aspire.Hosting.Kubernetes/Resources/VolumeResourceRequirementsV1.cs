// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the resource requirements for a Kubernetes volume.
/// </summary>
/// <remarks>
/// This class defines the limits and requests for the resources associated
/// with a volume in Kubernetes. It is primarily used to specify the
/// amount of resources (e.g., storage) requested or capped for a volume.
/// </remarks>
[AspireExport]
[YamlSerializable]
public sealed class VolumeResourceRequirementsV1
{
    /// <summary>
    /// Represents the upper bound or maximum resource usage constraints for a volume in a Kubernetes environment.
    /// This property specifies the resource limits, such as storage capacity or other volume-specific constraints,
    /// that a volume cannot exceed during its lifecycle.
    /// </summary>
    [YamlMember(Alias = "limits")]
    public Dictionary<string, string> Limits { get; } = [];

    /// <summary>
    /// Represents the minimum amount of compute resources required for a volume in a Kubernetes environment.
    /// Specifies the resource requests for the volume, such as storage capacity or other resource types.
    /// Used to define guaranteed resource allocation for the volume.
    /// </summary>
    [AspireExport]
    [YamlMember(Alias = "requests")]
    public Dictionary<string, string> Requests { get; } = [];
}
