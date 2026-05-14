// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents metadata for Kubernetes resources, encapsulating standard properties such as the resource's name, namespace, labels, annotations, and owner references.
/// </summary>
/// <remarks>
/// This class is used to define and handle key metadata information associated with Kubernetes objects, such as:
/// - Unique identifier (UID) of the resource.
/// - Name and namespace of the resource.
/// - Labels and annotations for organizing and categorizing resources.
/// - Managed fields for tracking changes to the resource.
/// - Owner references to define dependencies between resources.
/// - Creation and deletion timestamps, along with optional deletion grace period.
/// It is a core component for properly managing Kubernetes resources and ensuring compliance with Kubernetes object standards.
/// </remarks>
[YamlSerializable]
[AspireExport]
public sealed class ObjectMetaV1
{
    /// <summary>
    /// Gets or sets the unique identifier (UID) of the Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// This property is a globally unique identifier assigned to the resource by the Kubernetes system.
    /// It remains constant throughout the resource's lifecycle and is used to distinguish it from
    /// other resources, even if they share the same name and namespace.
    /// </remarks>
    [YamlMember(Alias = "uid")]
    public string Uid { get; set; } = null!;

    /// <summary>
    /// Specifies a prefix to be used by the system for generating a unique name if the `Name` property is not provided.
    /// </summary>
    /// <remarks>
    /// When set, the system ensures uniqueness by appending a unique identifier to the value of `GenerateName`. This is
    /// commonly used in scenarios where the exact name is not critical but must not collide with existing names.
    /// </remarks>
    [YamlMember(Alias = "generateName")]
    public string GenerateName { get; set; } = null!;

    /// <summary>
    /// Gets or sets the name of the Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// The `Name` property represents a unique identifier within the specified namespace
    /// for this resource. It is a required property when defining a Kubernetes resource and
    /// must conform to Kubernetes naming conventions.
    /// </remarks>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the namespace of the Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// The namespace serves as a mechanism to isolate and organize resources within a Kubernetes cluster.
    /// Each resource in a Kubernetes cluster can optionally belong to a namespace, which helps to group related resources together and manage their lifecycle independently.
    /// When not explicitly specified, resources may default to belonging to the "default" namespace.
    /// </remarks>
    [YamlMember(Alias = "namespace")]
    public string Namespace { get; set; } = null!;

    /// <summary>
    /// Gets or sets the self-referential link for the resource.
    /// </summary>
    /// <remarks>
    /// This property provides a URL that uniquely identifies the resource within the API.
    /// It is typically used for retrieving, updating, or tracking the resource programmatically.
    /// </remarks>
    [YamlMember(Alias = "selfLink")]
    public string SelfLink { get; set; } = null!;

    /// <summary>
    /// Represents the generation of the resource in the Kubernetes object metadata.
    /// </summary>
    /// <remarks>
    /// The generation is a sequence identifier that is incremented by the Kubernetes system
    /// to indicate changes to the desired state of the resource, as specified by the user.
    /// Changes in this value can be used to track updates to the resource specification.
    /// </remarks>
    [YamlMember(Alias = "generation")]
    public long? Generation { get; set; }

    /// <summary>
    /// Represents the specific version of a Kubernetes resource as stored in the server's database.
    /// </summary>
    /// <remarks>
    /// The ResourceVersion property is used to track changes to a Kubernetes resource. It is primarily
    /// used for optimistic concurrency control and to ensure that modifications to a resource do not
    /// conflict with other concurrent updates. The value of this property changes every time the
    /// resource is updated.
    /// </remarks>
    [YamlMember(Alias = "resourceVersion")]
    public string ResourceVersion { get; set; } = null!;

    /// <summary>
    /// Represents the timestamp indicating when the resource was created.
    /// </summary>
    /// <remarks>
    /// This property typically stores the date and time at which the resource was initially constructed.
    /// It may be null if the creation timestamp is not set or the resource has not been persisted yet.
    /// </remarks>
    [YamlMember(Alias = "creationTimestamp")]
    public DateTime? CreationTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the timestamp marking when the object is scheduled for deletion.
    /// </summary>
    /// <remarks>
    /// This property represents the time at which a Kubernetes resource is marked for deletion.
    /// If set, it indicates that the object is in the process of being deleted, and should not
    /// be acted upon, except for finalizers which need to complete their cleanup work. The
    /// object will be deleted once all finalizers are completed.
    /// </remarks>
    [YamlMember(Alias = "deletionTimestamp")]
    public DateTime? DeletionTimestamp { get; set; }

    /// <summary>
    /// Represents a collection of annotations associated with a Kubernetes object metadata.
    /// </summary>
    /// <remarks>
    /// Annotations are key-value pairs used to store arbitrary non-identifying information about an object.
    /// They can be utilized by external tooling or for informational purposes. Unlike labels,
    /// annotations are not used to identify or group resources.
    /// </remarks>
    [YamlMember(Alias = "annotations")]
    public Dictionary<string, string> Annotations { get; } = [];

    /// <summary>
    /// Specifies the duration, in seconds, that a Kubernetes resource will remain in a pending deletion state
    /// after a deletion request is initiated.
    /// </summary>
    /// <remarks>
    /// This property is used to define a grace period for cleanup tasks or termination procedures
    /// before the resource is permanently removed.
    /// If not set, the default behavior or grace period defined by the system will be applied.
    /// </remarks>
    [YamlMember(Alias = "deletionGracePeriodSeconds")]
    public long? DeletionGracePeriodSeconds { get; set; }

    /// <summary>
    /// A list of strings that describes the finalization steps for a Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// Finalizers are used to ensure certain cleanup operations are completed before
    /// the resource is permanently removed. This property contains the names of finalizers
    /// associated with the resource, specifying additional actions or processes to
    /// be executed prior to deletion.
    /// Once all finalizers are cleared from the list, the resource deletion is finalized.
    /// Adding or removing entries in this list must align with the desired cleanup or
    /// finalization logic for the resource.
    /// </remarks>
    [YamlMember(Alias = "finalizers")]
    public List<string> Finalizers { get; } = [];

    /// <summary>
    /// A collection of key-value pairs used to organize and categorize Kubernetes resources.
    /// </summary>
    /// <remarks>
    /// Labels provide a mechanism to attach metadata to Kubernetes objects, enabling users to select
    /// and group resources. These labels can be utilized by controllers for policy application and
    /// management operations and can also assist in search and filtering processes.
    /// </remarks>
    [YamlMember(Alias = "labels")]
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>
    /// A collection of ManagedFieldsEntryV1 instances that provide metadata about field-level management in a Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// This property contains a list of entries that describe changes and management details for specific fields in the Kubernetes resource.
    /// Each entry provides information about the fields affected, the manager responsible for the changes, operations performed,
    /// the subresources modified, and the API version used during the modification.
    /// Primarily useful for understanding which components are interacting with and updating specific fields of the resource's metadata.
    /// </remarks>
    [YamlMember(Alias = "managedFields")]
    public List<ManagedFieldsEntryV1> ManagedFields { get; } = [];

    /// <summary>
    /// Represents a list of owner references for a Kubernetes object.
    /// </summary>
    /// <remarks>
    /// The <c>OwnerReferences</c> property contains metadata about the relationships between a Kubernetes object
    /// and its owners. Each owner reference defines the owning object's unique identifier, name, and control behavior.
    /// This property is typically used to manage cascading deletes and ensure proper ownership hierarchy within Kubernetes resources.
    /// </remarks>
    [YamlMember(Alias = "ownerReferences")]
    public List<OwnerReferenceV1> OwnerReferences { get; } = [];
}
