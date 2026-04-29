// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Kubernetes projected volume source.
/// </summary>
/// <remarks>
/// A projected volume maps several supported volume sources into one directory.
/// </remarks>
[YamlSerializable]
public sealed class ProjectedVolumeSourceV1
{
    /// <summary>
    /// Gets or sets the default file mode for files created from projected sources.
    /// </summary>
    [YamlMember(Alias = "defaultMode")]
    public int? DefaultMode { get; set; }

    /// <summary>
    /// Gets the list of volume sources to project into the volume.
    /// </summary>
    [YamlMember(Alias = "sources")]
    public List<VolumeProjectionV1> Sources { get; } = [];
}
