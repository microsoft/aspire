// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Serves as the foundational class for defining Kubernetes resources in the v1 API version.
/// </summary>
/// <remarks>
/// The BaseKubernetesResource class contains shared properties common to all Kubernetes resources,
/// such as Kind, ApiVersion, and Metadata. It acts as an abstract base for deriving specific
/// resource types and facilitates consistent handling of Kubernetes resource definitions.
/// </remarks>
[YamlSerializable]
public abstract class BaseKubernetesResource(string apiVersion, string kind) : BaseKubernetesObject(apiVersion, kind)
{
    /// <summary>
    /// Gets or sets the metadata for the Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// The metadata contains standard information such as the resource’s name, namespace, labels, annotations,
    /// and other Kubernetes-specific properties. It is encapsulated in an <see cref="ObjectMetaV1"/> object,
    /// which provides properties for managing the resource’s unique identifier (UID), name, namespace, generation,
    /// and other relevant details like annotations, labels, and owner references.
    /// </remarks>
    [AspireExport]
    [YamlMember(Alias = "metadata", Order = -1)]
    public ObjectMetaV1 Metadata { get; set; } = new();
}
