// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the specification of a Kubernetes PersistentVolumeClaim resource.
/// </summary>
/// <remarks>
/// A PersistentVolumeClaim (PVC) is a request for storage by a user.
/// It serves as an abstraction to request specific storage with desired attributes such as size and access modes.
/// This class defines the set of properties that can be configured for a PVC.
/// </remarks>
[YamlSerializable]
public sealed class PersistentVolumeClaimSpecV1
{
    /// <summary>
    /// Represents the data source for a Persistent Volume Claim (PVC) in Kubernetes.
    /// </summary>
    /// <remarks>
    /// The DataSource property is used to specify the reference to an existing resource
    /// that serves as the source for the volume. This can be used, for example, to populate
    /// a PVC using data from an existing snapshot or another volume object. Defaults to
    /// <see langword="null"/> so the property is omitted from the serialized YAML when no
    /// data source has been configured. Eagerly initializing this to <c>new()</c> would
    /// otherwise emit an invalid empty <c>dataSource: {}</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "dataSource")]
    public TypedLocalObjectReferenceV1? DataSource { get; set; }

    /// <summary>
    /// Gets or sets the name of the storage class required by the PersistentVolumeClaim.
    /// </summary>
    /// <remarks>
    /// The StorageClassName specifies the name of the StorageClass that governs dynamic volume
    /// provisioning or volume binding for the associated PersistentVolumeClaim. Setting this property
    /// allows the claim to specifically request a volume of a particular storage class. It should match
    /// the name of an existing StorageClass in the Kubernetes cluster.
    /// If set to null or empty, it indicates that the default StorageClass configured in the cluster
    /// will be used. If no default StorageClass is configured and this value is not set, the claim cannot
    /// be dynamically provisioned.
    /// </remarks>
    [YamlMember(Alias = "storageClassName")]
    public string StorageClassName { get; set; } = null!;

    /// <summary>
    /// Represents the name of the class associated with the attributes of the volume in the
    /// PersistentVolumeClaim specification.
    /// </summary>
    /// <remarks>
    /// This property specifies the class used for describing the attributes of the storage
    /// volume. It is useful when there is a need to define specific characteristics or
    /// classifications for the volume, such as performance tiers or additional storage settings.
    /// </remarks>
    [YamlMember(Alias = "volumeAttributesClassName")]
    public string VolumeAttributesClassName { get; set; } = null!;

    /// <summary>
    /// Specifies the volume mode for a PersistentVolumeClaim.
    /// This determines how data in the volume is accessed by pods using the claim.
    /// </summary>
    /// <remarks>
    /// Possible values include:
    /// - "Filesystem": The volume is mounted as a filesystem.
    /// - "Block": The volume is used as a raw block device.
    /// This property is important for ensuring compatibility between the volume type
    /// and the application accessing it.
    /// </remarks>
    [YamlMember(Alias = "volumeMode")]
    public string VolumeMode { get; set; } = null!;

    /// <summary>
    /// Gets or sets the name of the specific volume to be bound to the PersistentVolumeClaim.
    /// </summary>
    /// <remarks>
    /// This property allows the user to bind the PersistentVolumeClaim to a pre-existing PersistentVolume
    /// by its name. When specified, the claim is directly associated with the named volume.
    /// </remarks>
    [YamlMember(Alias = "volumeName")]
    public string VolumeName { get; set; } = null!;

    /// <summary>
    /// Specifies a reference to a data source object for the Persistent Volume Claim (PVC).
    /// This property allows referencing an object that can provision the volume dynamically,
    /// such as an external volume controller or resource in the Kubernetes cluster.
    /// </summary>
    /// <remarks>
    /// The referenced resource should match the type specified, and the reference includes
    /// details such as kind, name, namespace, and API group of the object.
    /// This enables integration with external or heterogeneous resources in a Kubernetes environment.
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML
    /// when no data source reference has been configured. Eagerly initializing this to
    /// <c>new()</c> would otherwise emit an invalid empty <c>dataSourceRef: {}</c> mapping
    /// that Kubernetes rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "dataSourceRef")]
    public TypedObjectReferenceV1? DataSourceRef { get; set; }

    /// <summary>
    /// Gets or sets the label selector used to filter Kubernetes resources.
    /// </summary>
    /// <remarks>
    /// The <c>Selector</c> property allows the specification of a set of filtering criteria using labels.
    /// It supports exact matches with key-value pairs (MatchLabels) as well as more complex filtering rules
    /// using label selector requirements (MatchExpressions). This property is commonly used to dynamically
    /// select resources like pods or volumes in Kubernetes deployments. Defaults to
    /// <see langword="null"/> so the property is omitted from the serialized YAML when no
    /// selector has been configured. Eagerly initializing this to <c>new()</c> would
    /// otherwise emit an invalid empty <c>selector: {}</c> mapping that Kubernetes rejects
    /// on apply.
    /// </remarks>
    [YamlMember(Alias = "selector")]
    public LabelSelectorV1? Selector { get; set; }

    /// <summary>
    /// Defines the access modes for a Persistent Volume Claim.
    /// </summary>
    /// <remarks>
    /// Access modes specify the type of access a pod has to the persistent volume. The supported access modes are:
    /// - ReadWriteOnce: The volume can be mounted as read-write by a single node.
    /// - ReadOnlyMany: The volume can be mounted as read-only by many nodes.
    /// - ReadWriteMany: The volume can be mounted as read-write by many nodes.
    /// </remarks>
    [YamlMember(Alias = "accessModes")]
    public List<string> AccessModes { get; } = [];

    /// <summary>
    /// Gets or sets the resource requirements for the volume.
    /// Defines the requested and limit capacities for the volume resources,
    /// including storage and other related attributes.
    /// </summary>
    [YamlMember(Alias = "resources")]
    public VolumeResourceRequirementsV1 Resources { get; set; } = new();
}
