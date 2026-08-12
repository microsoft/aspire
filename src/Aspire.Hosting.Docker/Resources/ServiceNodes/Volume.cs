// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources.ComposeNodes;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes;

/// <summary>
/// Represents a volume definition in a Docker Compose configuration file.
/// </summary>
/// <remarks>
/// The <see cref="Volume"/> class is used to define properties and options for volumes in a Docker environment.
/// Volumes are used to persist data beyond the lifecycle of a container and can be shared among multiple containers.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class Volume : NamedComposeMember
{
    /// <summary>
    /// Gets or sets the type of volume. This specifies the method of volume provisioning
    /// such as bind mounts, named volumes, or other supported volume types in Docker.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the target path inside the container where the volume is mounted.
    /// This specifies the container location for the volume's data.
    /// </summary>
    [YamlMember(Alias = "target")]
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the source property of the volume. The source defines the location on the host
    /// system or the specific resource from which the volume is sourced.
    /// </summary>
    [YamlMember(Alias = "source")]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the volume is mounted as read-only.
    /// </summary>
    /// <remarks>
    /// When set to true, the volume will be mounted with read-only permissions, preventing modification
    /// of the data within the container. This is commonly used to enforce data immutability for certain use cases.
    /// </remarks>
    [YamlMember(Alias = "read_only")]
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the name of the driver used for the volume.
    /// The driver is responsible for managing the volume and its storage backend.
    /// </summary>
    [YamlMember(Alias = "driver")]
    public string? Driver { get; set; }

    /// <summary>
    /// Represents a collection of driver-specific options for the volume.
    /// These options are passed as key-value pairs to the volume driver,
    /// allowing customization or configuration specific to the driver being used.
    /// </summary>
    [YamlMember(Alias = "driver_opts", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> DriverOpts { get; set; } = [];

    /// <summary>
    /// Indicates whether the volume is external to the current scope or environment.
    /// A value of <c>true</c> specifies that the volume is managed outside the scope of
    /// the current application or configuration. A value of <c>false</c>, or a null value,
    /// indicates that the volume is managed internally or by default behavior.
    /// </summary>
    [YamlMember(Alias = "external")]
    public bool? External { get; set; }

    /// <summary>
    /// Gets or sets a dictionary of labels associated with the volume.
    /// Labels are key-value pairs that can be used for metadata purposes
    /// or for organizing and identifying volumes within Docker services.
    /// </summary>
    [YamlMember(Alias = "labels", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Labels { get; set; } = [];
}
