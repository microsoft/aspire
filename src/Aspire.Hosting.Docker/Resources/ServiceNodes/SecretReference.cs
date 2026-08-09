// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes;

/// <summary>
/// Represents a reference to a secret within a Docker service configuration.
/// </summary>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class SecretReference
{
    /// <summary>
    /// Gets or sets the name of the source secret reference.
    /// This property is used to specify the source from which a secret or configuration is derived.
    /// </summary>
    [YamlMember(Alias = "source")]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the target path where the secret will be mounted within the container.
    /// This path is used to specify the destination location of the secret in the container's file system.
    /// </summary>
    [YamlMember(Alias = "target")]
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the user ID (UID) associated with the secret reference.
    /// This property allows specifying the UID that will be assigned to the secret
    /// when it is mounted within a container.
    /// </summary>
    [YamlMember(Alias = "uid")]
    public int? Uid { get; set; }

    /// <summary>
    /// Represents the group ID (GID) associated with the secret reference in a Docker service node.
    /// </summary>
    /// <remarks>
    /// This property defines the group ID of the user for accessing the secret. It is optional and may be null if not specified.
    /// </remarks>
    [YamlMember(Alias = "gid")]
    public int? Gid { get; set; }

    /// <summary>
    /// Gets or sets the file mode for the secret reference.
    /// The mode defines the file permissions that will be applied to the secret when mounted.
    /// This is represented as an integer value.
    /// </summary>
    [YamlMember(Alias = "mode")]
    public int? Mode { get; set; }
}
