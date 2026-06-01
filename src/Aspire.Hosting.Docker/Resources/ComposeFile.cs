// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.Docker.Resources;

/// <summary>
/// Represents a Docker Compose file with properties and configurations for services, networks, volumes, secrets, configs, and custom extensions.
/// </summary>
/// <remarks>
/// This class is designed to encapsulate the structure of a Docker Compose file as a strongly-typed model.
/// It supports serialization to YAML format for usage in Docker Compose operations.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class ComposeFile
{
    /// <summary>
    /// Represents the name of the Docker Compose file or project.
    /// </summary>
    /// <remarks>
    /// The name property is used to identify the Compose application
    /// or project when orchestrating Docker containers.
    /// </remarks>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Represents the version of the Docker Compose file format being used.
    /// This property specifies the format of the Compose file and determines the supported features and behaviors.
    /// </summary>
    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    /// <summary>
    /// Represents a collection of services defined in a Docker Compose file.
    /// Each service is identified by a unique name and contains configuration details
    /// as defined by the <see cref="Service"/> class.
    /// </summary>
    /// <remarks>
    /// Services are a critical part of the Docker Compose ecosystem and are used to define
    /// individual application components. These components can include images, commands,
    /// environment variables, ports, volumes, dependencies, and more.
    /// </remarks>
    /// <value>
    /// A dictionary where the key is the name of the service (as a string), and the value
    /// is a <see cref="Service"/> object containing the configuration of the service.
    /// </value>
    [YamlMember(Alias = "services", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Service> Services { get; set; } = [];

    /// <summary>
    /// Represents the collection of networks defined in a Docker Compose file.
    /// </summary>
    /// <remarks>
    /// Each key in the dictionary represents the name of the network, and the value is an instance of the <see cref="Network"/> class,
    /// which encapsulates the details and configurations of the corresponding network.
    /// </remarks>
    [YamlMember(Alias = "networks", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Network> Networks { get; set; } = [];

    /// <summary>
    /// Represents a collection of volume definitions within a Docker Compose file.
    /// </summary>
    /// <remarks>
    /// The volumes are defined using a dictionary structure where the key represents the
    /// name of the volume, and the value is an instance of the <see cref="Volume"/> class.
    /// Volumes can be used to share data between containers or between a container and the host system.
    /// </remarks>
    [YamlMember(Alias = "volumes", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Volume> Volumes { get; set; } = [];

    /// <summary>
    /// Represents the secrets section in a Docker Compose file.
    /// Contains a collection of secret definitions used within the Compose file.
    /// </summary>
    /// <remarks>
    /// Each secret is represented as a key-value pair, where the key is the name
    /// of the secret, and the value is an instance of the <see cref="Secret"/> class,
    /// which holds the details about the secret such as file location, external status,
    /// and additional metadata like labels.
    /// </remarks>
    [YamlMember(Alias = "secrets", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Secret> Secrets { get; set; } = [];

    /// <summary>
    /// Represents a collection of configuration objects within a Docker Compose file.
    /// Each key in the dictionary corresponds to a configuration name, and the value is
    /// an instance of the <see cref="Config"/> class that contains the associated configuration details.
    /// </summary>
    [YamlMember(Alias = "configs", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Config> Configs { get; set; } = [];

    /// <summary>
    /// Represents a collection of user-defined extension fields that can be
    /// added to the Compose file. These extensions are represented as a dictionary
    /// where the key is a string identifier for the extension, and the value is
    /// an object that holds the custom data relevant to the extension. This allows
    /// flexibility for including additional metadata or configuration outside the scope
    /// of standard Compose file specifications.
    /// </summary>
    [YamlMember(Alias = "extensions", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, object> Extensions { get; set; } = [];

    /// <summary>
    /// Adds a new network to the Compose file.
    /// </summary>
    /// <param name="network">The network instance to add to the Compose file.</param>
    /// <returns>The updated <see cref="ComposeFile"/> instance with the added network.</returns>
    public ComposeFile AddNetwork(Network network)
    {
        Networks[network.Name] = network;
        return this;
    }

    /// <summary>
    /// Adds a new service to the Compose file.
    /// </summary>
    /// <param name="service">The service instance to add to the Compose file.</param>
    /// <returns>The updated <see cref="ComposeFile"/> instance containing the added service.</returns>
    public ComposeFile AddService(Service service)
    {
        Services[service.Name] = service;
        return this;
    }

    /// <summary>
    /// Adds a new volume to the Compose file.
    /// </summary>
    /// <param name="volume">The volume instance to add to the Compose file.</param>
    /// <returns>The updated <see cref="ComposeFile"/> instance with the added volume.</returns>
    public ComposeFile AddVolume(Volume volume)
    {
        Volumes[volume.Name] = volume;
        return this;
    }

    /// <summary>
    /// Adds a new config entry to the Compose file.
    /// </summary>
    /// <param name="config">The config instance to add to the Compose file.</param>
    /// <returns>The updated <see cref="ComposeFile"/> instance with the added config.</returns>
    public ComposeFile AddConfig(Config config)
    {
        Configs[config.Name] = config;
        return this;
    }

    /// <summary>
    /// Converts the current instance of <see cref="ComposeFile"/> to its YAML string representation.
    /// </summary>
    /// <param name="lineEndings">Specifies the line endings to be used in the serialized YAML output. Defaults to "\n".</param>
    /// <returns>A string containing the YAML representation of the <see cref="ComposeFile"/> instance.</returns>
    public string ToYaml(string lineEndings = "\n")
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new UnixFileModeTypeConverter())
            .WithEventEmitter(nextEmitter => new StringSequencesFlowStyle(nextEmitter))
            .WithEventEmitter(nextEmitter => new ForceQuotedStringsEventEmitter(nextEmitter))
            .WithEmissionPhaseObjectGraphVisitor(args => new YamlIEnumerableSkipEmptyObjectGraphVisitor(args.InnerVisitor))
            .WithNewLine(lineEndings)
            .WithIndentedSequences()
            .Build();

        return serializer.Serialize(this);
    }
}
