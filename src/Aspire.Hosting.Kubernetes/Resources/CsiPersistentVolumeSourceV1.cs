// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Container Storage Interface (CSI) persistent volume source.
/// </summary>
/// <remarks>
/// This type maps to the Kubernetes <c>CSIPersistentVolumeSource</c> schema.
/// See <see href="https://kubernetes.io/docs/reference/kubernetes-api/config-and-storage-resources/persistent-volume-v1/#CSIPersistentVolumeSource"/>.
/// </remarks>
[YamlSerializable]
public sealed class CsiPersistentVolumeSourceV1
{
    /// <summary>
    /// Gets or sets the name of the CSI driver that handles the volume.
    /// </summary>
    [YamlMember(Alias = "driver")]
    public string Driver { get; set; } = null!;

    /// <summary>
    /// Gets or sets the unique volume identifier understood by the CSI driver.
    /// </summary>
    [YamlMember(Alias = "volumeHandle")]
    public string VolumeHandle { get; set; } = null!;

    /// <summary>
    /// Gets or sets the filesystem type to mount.
    /// </summary>
    [YamlMember(Alias = "fsType")]
    public string? FileSystemType { get; set; }

    /// <summary>
    /// Gets or sets whether the volume is mounted read-only.
    /// </summary>
    [YamlMember(Alias = "readOnly")]
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// Gets the driver-specific volume attributes.
    /// </summary>
    [YamlMember(Alias = "volumeAttributes")]
    public Dictionary<string, string> VolumeAttributes { get; } = [];
}
