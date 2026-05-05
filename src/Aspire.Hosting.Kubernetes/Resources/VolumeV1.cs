// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a volume configuration definition within a Kubernetes pod.
/// This class allows specifying different types of volume sources such as Image, HostPath,
/// Persistent Volume Claim, ConfigMap, Secret, Projected, and others, enabling configuration of data storage in a pod.
/// </summary>
[YamlSerializable]
public sealed class VolumeV1
{
    /// <summary>
    /// Represents the configuration for an image-based volume source within a Kubernetes Volume definition.
    /// </summary>
    /// <remarks>
    /// This property allows specification of an image-based volume with details on the container image
    /// and the image pulling policy. It is utilized in Kubernetes resources to define volumes that rely on
    /// container images for their content source.
    /// </remarks>
    [YamlMember(Alias = "image")]
    public ImageVolumeSourceV1? Image { get; set; }

    /// <summary>
    /// Gets or sets the name of the volume.
    /// This property is used to identify the volume within the context of a Kubernetes resource.
    /// It is a required value and must be unique among all defined volumes in a specific resource.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the HostPath volume source for the volume.
    /// HostPath volume sources allow mounting a file or directory
    /// from the host node's filesystem into a pod. This is typically
    /// used for scenarios like accessing host filesystem resources
    /// or sharing data between containers in a pod.
    /// </summary>
    [YamlMember(Alias = "hostPath")]
    public HostPathVolumeSourceV1? HostPath { get; set; }

    /// <summary>
    /// Gets or sets the configuration for an ephemeral volume associated with the resource.
    /// An ephemeral volume is a transient storage volume tied to the lifecycle of a pod.
    /// This property allows specifying the template for a PersistentVolumeClaim
    /// that defines the parameters of the ephemeral volume.
    /// </summary>
    [YamlMember(Alias = "ephemeral")]
    public EphemeralVolumeSourceV1? Ephemeral { get; set; }

    /// <summary>
    /// Represents a PersistentVolumeClaim (PVC) that will be mounted as a volume in a Kubernetes environment.
    /// A PVC is a request for storage by a user, and this property links the volume configuration to an existing claim.
    /// </summary>
    [YamlMember(Alias = "persistentVolumeClaim")]
    public PersistentVolumeClaimVolumeSourceV1? PersistentVolumeClaim { get; set; }

    /// <summary>
    /// Represents the Kubernetes ConfigMap volume source configuration.
    /// </summary>
    /// <remarks>
    /// The ConfigMap property allows mounting a Kubernetes ConfigMap as a volume within a container.
    /// It enables accessing key-value data from a ConfigMap directly within the container's file system.
    /// </remarks>
    [YamlMember(Alias = "configMap")]
    public ConfigMapVolumeSourceV1? ConfigMap { get; set; }

    /// <summary>
    /// Gets or sets the configuration for an EmptyDir volume source in Kubernetes.
    /// An EmptyDir volume is a temporary storage directory that is created empty when a pod is assigned to a node.
    /// The volume's contents only exist for the lifetime of the pod and will be deleted when the pod is removed.
    /// </summary>
    [YamlMember(Alias = "emptyDir")]
    public EmptyDirVolumeSourceV1? EmptyDir { get; set; }

    /// <summary>
    /// Represents a secret volume source in Kubernetes.
    /// This property is used to specify configuration details
    /// for a volume that retrieves data from a Kubernetes Secret resource.
    /// </summary>
    [YamlMember(Alias = "secret")]
    public SecretVolumeSourceV1? Secret { get; set; }

    /// <summary>
    /// Gets or sets the configuration for a projected volume source.
    /// </summary>
    /// <remarks>
    /// Projected volumes combine several volume sources, such as Secrets and ConfigMaps,
    /// into a single directory mounted into the pod.
    /// </remarks>
    [YamlMember(Alias = "projected")]
    public ProjectedVolumeSourceV1? Projected { get; set; }
}
