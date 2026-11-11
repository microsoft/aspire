// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a reference to an object across different API versions in Kubernetes.
/// </summary>
/// <remarks>
/// The CrossVersionObjectReferenceV1 class is used to specify a reference to a Kubernetes object,
/// irrespective of the object's API version. It contains details such as the name
/// of the referenced object.
/// </remarks>
[YamlSerializable]
public sealed class CrossVersionObjectReferenceV1() : BaseKubernetesObject("v1", "ObjectReference")
{
    /// <summary>
    /// Gets or sets the name of the Kubernetes resource being referenced.
    /// </summary>
    /// <remarks>
    /// The Name property specifies the name of the Kubernetes object that is being identified or targeted.
    /// It is a required value that enables the user to refer to a specific resource within the desired namespace.
    /// </remarks>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;
}
