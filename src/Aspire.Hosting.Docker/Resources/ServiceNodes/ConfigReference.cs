// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes;

/// <summary>
/// Represents a reference to a configuration used within a service resource.
/// </summary>
/// <remarks>
/// This class is typically used to define configurations associated with a
/// service in a containerized environment. It includes information about
/// the configuration source, target, ownership, and permissions.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class ConfigReference
{
    /// <summary>
    /// Gets or sets the source configuration reference.
    /// This property specifies the origin of the configuration file or data required by the service node.
    /// </summary>
    [YamlMember(Alias = "source")]
    public string? Source { get; set; }

    /// <summary>
    /// Specifies the target location where the referenced configuration data
    /// will be applied or mounted in the context of the Docker service.
    /// </summary>
    [YamlMember(Alias = "target")]
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the user identifier (UID) associated with the configuration reference.
    /// Optional property that specifies the user ID for accessing the configuration target.
    /// </summary>
    [YamlMember(Alias = "uid")]
    public string? Uid { get; set; }

    /// <summary>
    /// Gets or sets the group ID (GID) used to identify the group of the referenced configuration.
    /// </summary>
    /// <remarks>
    /// The GID is an optional integer parameter that specifies the group ownership
    /// for the resource. If set, it defines the group to which the target resource belongs.
    /// </remarks>
    [YamlMember(Alias = "gid")]
    public string? Gid { get; set; }

    /// <summary>
    /// Represents the access mode for the configuration reference in the form of an integer value.
    /// This property determines the permissions or access level for the configuration being referenced.
    /// Typical values might correspond to standard file permission modes.
    /// </summary>
    [YamlMember(Alias = "mode")]
    public UnixFileMode? Mode { get; set; }
}
