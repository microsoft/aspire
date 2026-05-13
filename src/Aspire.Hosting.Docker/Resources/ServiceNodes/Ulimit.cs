// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes;

/// <summary>
/// Represents the configuration for system resource limits (ulimits) for a container.
/// </summary>
/// <remarks>
/// This class is typically used to specify soft and hard limits for resources
/// such as file descriptors or process count for Docker containers.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class Ulimit
{
    /// <summary>
    /// Defines the soft limit for the Ulimit configuration.
    /// The soft limit is the value for resource restrictions that a process is allowed to increase up to the hard limit.
    /// This property is nullable, which indicates that this configuration might not be set.
    /// </summary>
    [YamlMember(Alias = "soft")]
    public int? Soft { get; set; }

    /// <summary>
    /// Gets or sets the hard limit for the resource control.
    /// </summary>
    /// <remarks>
    /// The hard limit defines the maximum value that cannot be exceeded.
    /// It is usually set by an administrator and imposes a strict upper boundary.
    /// This property can be null, indicating no hard limit is set.
    /// </remarks>
    [YamlMember(Alias = "hard")]
    public int? Hard { get; set; }
}
