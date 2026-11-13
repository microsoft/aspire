// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a CSI (Container Storage Interface) volume source in Kubernetes.
/// </summary>
/// <remarks>
/// CSI volumes are inline ephemeral volumes that are served by a CSI driver.
/// They can be used by drivers to project external storage, secrets, or certificates
/// into a pod at mount time.
/// </remarks>
[YamlSerializable]
public sealed class CsiVolumeSourceV1
{
    /// <summary>
    /// Gets or sets the name of the CSI driver that handles the volume.
    /// </summary>
    /// <remarks>
    /// The driver name identifies the CSI driver registered in the cluster that
    /// should publish this volume for the pod.
    /// </remarks>
    [YamlMember(Alias = "driver")]
    public string Driver { get; set; } = null!;

    /// <summary>
    /// Gets or sets a value indicating whether the volume should be mounted read-only.
    /// </summary>
    /// <remarks>
    /// If set to <see langword="true"/>, Kubernetes requests a read-only mount from the
    /// CSI driver. When omitted, Kubernetes treats the value as <see langword="false"/>,
    /// so the volume is mounted read/write.
    /// </remarks>
    [YamlMember(Alias = "readOnly")]
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the filesystem type to mount.
    /// </summary>
    /// <remarks>
    /// This value is passed to the CSI driver for volume publishing. It is used by
    /// filesystem-backed CSI drivers and may be ignored by drivers that do not mount
    /// a filesystem.
    /// </remarks>
    [YamlMember(Alias = "fsType")]
    public string? FsType { get; set; }

    /// <summary>
    /// Gets the driver-specific volume attributes.
    /// </summary>
    /// <remarks>
    /// Each key-value pair is passed to the CSI driver. For example, the Secrets Store
    /// CSI driver uses this collection to identify the SecretProviderClass for the pod.
    /// </remarks>
    [YamlMember(Alias = "volumeAttributes")]
    public Dictionary<string, string> VolumeAttributes { get; } = [];

    /// <summary>
    /// Gets or sets a reference to the secret that contains node publish credentials.
    /// </summary>
    /// <remarks>
    /// Some CSI drivers use this reference to authenticate node publish operations.
    /// The referenced object is a Kubernetes Secret in the same namespace as the pod.
    /// </remarks>
    [YamlMember(Alias = "nodePublishSecretRef")]
    public LocalObjectReferenceV1? NodePublishSecretRef { get; set; }
}
