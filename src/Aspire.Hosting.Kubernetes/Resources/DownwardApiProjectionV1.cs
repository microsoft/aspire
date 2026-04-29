// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Downward API source within a projected volume.
/// </summary>
[YamlSerializable]
public sealed class DownwardApiProjectionV1
{
    /// <summary>
    /// Gets the files to project from pod and container fields.
    /// </summary>
    [YamlMember(Alias = "items")]
    public List<DownwardApiVolumeFileV1> Items { get; } = [];
}
