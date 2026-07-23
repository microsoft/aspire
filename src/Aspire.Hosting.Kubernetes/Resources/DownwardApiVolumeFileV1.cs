// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Downward API file projected into a volume.
/// </summary>
[YamlSerializable]
public sealed class DownwardApiVolumeFileV1
{
    /// <summary>
    /// Gets or sets the pod field to project into the file.
    /// </summary>
    [YamlMember(Alias = "fieldRef")]
    public ObjectFieldSelectorV1? FieldRef { get; set; }

    /// <summary>
    /// Gets or sets the file mode for the projected file.
    /// </summary>
    [YamlMember(Alias = "mode")]
    public int? Mode { get; set; }

    /// <summary>
    /// Gets or sets the relative path of the projected file.
    /// </summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = null!;

    /// <summary>
    /// Gets or sets the container resource field to project into the file.
    /// </summary>
    [YamlMember(Alias = "resourceFieldRef")]
    public ResourceFieldSelectorV1? ResourceFieldRef { get; set; }
}
