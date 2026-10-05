// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the specification for a Kubernetes Persistent Volume.
/// Defines the details of storage and access configurations for a PersistentVolume resource.
/// </summary>
[YamlSerializable]
public sealed class PersistentVolumeSpecV1
{
    /// <summary>
    /// Specifies the name of the StorageClass associated with this persistent volume.
    /// The StorageClass provides dynamic provisioning parameters and policies for the
    /// storage resource. If no StorageClass is defined, the default StorageClass for
    /// the cluster will be applied if available.
    /// </summary>
    [YamlMember(Alias = "storageClassName")]
    public string StorageClassName { get; set; } = null!;

    /// <summary>
    /// Specifies the class name associated with volume attributes in a Kubernetes PersistentVolume specification.
    /// This property is typically used to indicate a custom class of attributes assigned to the persistent volume,
    /// enabling more granular configurations or specific behaviors for the volume.
    /// </summary>
    [YamlMember(Alias = "volumeAttributesClassName")]
    public string VolumeAttributesClassName { get; set; } = null!;

    /// <summary>
    /// Specifies the volume mode of the persistent volume.
    /// </summary>
    /// <remarks>
    /// Defines the way the volume is mounted. Typical values include "Filesystem" or "Block".
    /// This property is required to determine how the volume will be used and accessed.
    /// </remarks>
    [YamlMember(Alias = "volumeMode")]
    public string VolumeMode { get; set; } = null!;

    /// <summary>
    /// Gets or sets the reference to a claim within the Kubernetes cluster that is bound to the PersistentVolume.
    /// This property links the PersistentVolume to a specific PersistentVolumeClaim (PVC) via an object reference,
    /// allowing resources to be dynamically or statically provisioned as required.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML
    /// when no claim reference has been configured. Eagerly initializing this to <c>new()</c>
    /// would otherwise emit an invalid empty <c>claimRef: {}</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "claimRef")]
    public ObjectReferenceV1? ClaimRef { get; set; }

    /// <summary>
    /// Gets or sets the configuration for a HostPath volume in a Kubernetes PersistentVolume.
    /// This property specifies the details of a HostPath volume source, allowing a directory
    /// from the host node file system to be mounted into a pod's container.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML
    /// when no host path has been configured. Eagerly initializing this to <c>new()</c>
    /// would otherwise emit an invalid empty <c>hostPath: {}</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "hostPath")]
    public HostPathVolumeSourceV1? HostPath { get; set; }

    /// <summary>
    /// Gets or sets the configuration for a local volume source.
    /// </summary>
    /// <remarks>
    /// This property specifies the configuration for a local PersistentVolume in Kubernetes,
    /// represented by the <see cref="LocalVolumeSourceV1" /> type. Local volumes are backed
    /// by a specific file or directory on the node where the volume is created.
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML
    /// when no local volume source has been configured. Eagerly initializing this to
    /// <c>new()</c> would otherwise emit an invalid empty <c>local: {}</c> mapping that
    /// Kubernetes rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "local")]
    public LocalVolumeSourceV1? Local { get; set; }

    /// <summary>
    /// Represents the access modes for a Persistent Volume in a Kubernetes cluster.
    /// AccessModes define the ways in which the volume can be mounted and utilized:
    /// - ReadWriteOnce: The volume can be mounted as read-write by a single node.
    /// - ReadOnlyMany: The volume can be mounted read-only by many nodes.
    /// - ReadWriteMany: The volume can be mounted as read-write by many nodes.
    /// </summary>
    [YamlMember(Alias = "accessModes")]
    public List<string> AccessModes { get; } = [];

    /// <summary>
    /// Specifies a list of mount options for a persistent volume.
    /// Mount options are passed to the mount binary (e.g., NFS, Ceph) for configuring the volume at mount time.
    /// These options allow customization of mount behavior based on the filesystem or volume type.
    /// </summary>
    [YamlMember(Alias = "mountOptions")]
    public List<string> MountOptions { get; } = [];

    /// <summary>
    /// Represents the storage capacity of the persistent volume.
    /// The keys in the dictionary represent capacity types (e.g., "storage"),
    /// and the values define the corresponding quantity for the capacity type.
    /// </summary>
    [YamlMember(Alias = "capacity")]
    public Dictionary<string, string> Capacity { get; set; } = [];

    /// <summary>
    /// Specifies constraints that limit which nodes a persistent volume can be accessed from.
    /// </summary>
    /// <remarks>
    /// The NodeAffinity property defines the rules and requirements for associating a
    /// PersistentVolume with specific nodes in the Kubernetes cluster. It describes the
    /// node selection criteria, which help ensure proper placement of the volume based
    /// on node attributes and conditions. Defaults to <see langword="null"/> so the
    /// property is omitted from the serialized YAML when no node affinity has been
    /// configured. Eagerly initializing this to <c>new()</c> would otherwise emit an
    /// invalid empty <c>nodeAffinity: { required: {} }</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "nodeAffinity")]
    public VolumeNodeAffinityV1? NodeAffinity { get; set; }

    /// <summary>
    /// Describes the reclaim policy of a PersistentVolume in Kubernetes.
    /// The reclaim policy determines how a PersistentVolume should be treated
    /// when it is released from its associated PersistentVolumeClaim.
    /// Typical values include:
    /// - "Retain": Retains the volume for manual recovery.
    /// - "Recycle": Cleans the volume by removing its contents for reuse.
    /// - "Delete": Deletes the volume from storage.
    /// </summary>
    [YamlMember(Alias = "persistentVolumeReclaimPolicy")]
    public string PersistentVolumeReclaimPolicy { get; set; } = null!;
}
