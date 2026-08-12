// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources.ServiceNodes;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ComposeNodes;

/// <summary>
/// Represents a Docker network configuration as part of a Compose file.
/// </summary>
/// <remarks>
/// This class encapsulates the properties and options related to a network in a Docker Compose file.
/// It includes configurations such as driver type, options, labels, IPAM settings, and more.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class Network : NamedComposeMember
{
    /// <summary>
    /// Gets or sets the driver used for the network. The driver determines the networking implementation
    /// that the container network is based on. Examples include bridge, overlay, host, etc.
    /// </summary>
    [YamlMember(Alias = "driver")]
    public string? Driver { get; set; }

    /// <summary>
    /// Represents a dictionary of driver-specific options for the network configuration in a Docker service node.
    /// </summary>
    /// <remarks>
    /// These options are key-value pairs that allow customization of network settings based on the specified driver.
    /// They can control behaviors or features unique to the driver being used.
    /// </remarks>
    [YamlMember(Alias = "driver_opts", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> DriverOpts { get; set; } = [];

    /// <summary>
    /// Indicates whether the network is external or managed by Docker outside of the
    /// application stack. When set to true, the network is assumed to be pre-existing
    /// and not defined by the application's configuration. When set to false or null,
    /// the network can be defined and created within the application scope.
    /// </summary>
    [YamlMember(Alias = "external")]
    public bool? External { get; set; }

    /// <summary>
    /// Represents a collection of metadata labels applied to the network configuration.
    /// These labels can be used to organize, manage, or identify network resources.
    /// </summary>
    [YamlMember(Alias = "labels", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>
    /// Represents the IP Address Management (IPAM) configuration for a network in a container environment.
    /// IPAM is used to manage network configurations such as custom IP ranges, subnets, and gateway settings.
    /// </summary>
    [YamlMember(Alias = "ipam")]
    public Ipam? Ipam { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the network is attachable.
    /// When this property is set to true, containers can dynamically attach to the network
    /// at runtime. This feature is primarily used in Docker Swarm mode to enable service discovery
    /// and communication between services.
    /// </summary>
    [YamlMember(Alias = "attachable")]
    public bool? Attachable { get; set; }

    /// <summary>
    /// Represents whether the network is configured as an ingress network.
    /// An ingress network is used to manage the internal routing and load balancing
    /// for swarm services in Docker.
    /// </summary>
    [YamlMember(Alias = "ingress")]
    public bool? Ingress { get; set; }

    /// <summary>
    /// Determines if the network is restricted to internal usage only.
    /// When set to true, the network is not accessible externally.
    /// </summary>
    [YamlMember(Alias = "internal")]
    public bool? Internal { get; set; }
}
