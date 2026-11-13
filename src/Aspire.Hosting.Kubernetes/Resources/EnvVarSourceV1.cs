// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// EnvVarSourceV1 represents a source for an environment variable value in a Kubernetes resource.
/// It provides multiple options to derive the value of an environment variable, such as from
/// a specific key in a ConfigMap, a field reference within the resource, a resource field (e.g., CPU or memory usage),
/// a specific key in a Secret, or a key in an environment variable file.
/// </summary>
[YamlSerializable]
public sealed class EnvVarSourceV1
{
    /// <summary>
    /// ConfigMapKeyRef specifies a reference to a specific key within a Kubernetes ConfigMap.
    /// It allows configuring environment variables from a ConfigMap by selecting a key and its associated value.
    /// This property can be used for configuring your application with data stored in ConfigMaps, with the option to make the reference optional.
    /// </summary>
    [YamlMember(Alias = "configMapKeyRef")]
    public ConfigMapKeySelectorV1? ConfigMapKeyRef { get; set; }

    /// <summary>
    /// FieldRef specifies an ObjectFieldSelector which selects an APIVersioned
    /// field of an object. This can be used to extract specific pieces of
    /// information from object metadata or specification.
    /// </summary>
    [YamlMember(Alias = "fieldRef")]
    public ObjectFieldSelectorV1? FieldRef { get; set; }

    /// <summary>
    /// Gets or sets a reference to a key in an environment variable file in an emptyDir volume.
    /// Requires Kubernetes 1.34 or later with the EnvFiles feature enabled.
    /// </summary>
    [YamlMember(Alias = "fileKeyRef")]
    public FileKeySelectorV1? FileKeyRef { get; set; }

    /// <summary>
    /// ResourceFieldRef is a property representing a reference to container resource fields, such as CPU or Memory.
    /// It leverages the ResourceFieldSelectorV1 type, which allows specification of container resource attributes and their output formats.
    /// </summary>
    [YamlMember(Alias = "resourceFieldRef")]
    public ResourceFieldSelectorV1? ResourceFieldRef { get; set; }

    /// <summary>
    /// SecretKeyRef defines a reference to a specific key within a Kubernetes Secret.
    /// It allows the user to map a particular secret key to an environment variable in a container.
    /// </summary>
    /// <remarks>
    /// This property is used to securely provide sensitive data, such as tokens or passwords, to an application.
    /// The referenced key must exist within the specified Secret, and an optional flag can dictate whether the absence
    /// of the key is tolerable.
    /// </remarks>
    [YamlMember(Alias = "secretKeyRef")]
    public SecretKeySelectorV1? SecretKeyRef { get; set; }
}
