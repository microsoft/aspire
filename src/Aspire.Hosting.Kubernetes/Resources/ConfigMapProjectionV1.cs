// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a ConfigMap source within a projected volume.
/// </summary>
[YamlSerializable]
public sealed class ConfigMapProjectionV1
{
    /// <summary>
    /// Gets the key-to-path mappings to project from the ConfigMap.
    /// </summary>
    [YamlMember(Alias = "items")]
    public List<KeyToPathV1> Items { get; } = [];

    /// <summary>
    /// Gets or sets the name of the ConfigMap to project.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets whether the ConfigMap or selected keys are optional.
    /// </summary>
    [YamlMember(Alias = "optional")]
    public bool? Optional { get; set; }
}
