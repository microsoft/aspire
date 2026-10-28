// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Kubernetes PersistentVolumeClaim resource in the v1 API version.
/// </summary>
/// <remarks>
/// PersistentVolumeClaims (PVCs) are requests for storage by users.
/// They abstract the details of the underlying storage resource to provide dynamic or static provisioned storage
/// within a Kubernetes cluster.
/// This class encapsulates the specification and metadata of a PersistentVolumeClaim resource.
/// </remarks>
[AspireExport]
[YamlSerializable]
public sealed class PersistentVolumeClaim() : BaseKubernetesResource("v1", "PersistentVolumeClaim")
{
    /// <summary>
    /// Gets or sets the specification of the PersistentVolumeClaim (PVC) resource.
    /// </summary>
    /// <remarks>
    /// Defines the desired properties and configuration of the PersistentVolumeClaim.
    /// This includes storage requirements, access modes, data sources, and selectors.
    /// The <see cref="PersistentVolumeClaimSpecV1"/> type provides a complete set of
    /// configurable settings for the PVC.
    /// </remarks>
    [AspireExport]
    [YamlMember(Alias = "spec")]
    public PersistentVolumeClaimSpecV1 Spec { get; set; } = new();
}
