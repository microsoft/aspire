// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ComposeNodes;

/// <summary>
/// Represents a Docker Compose service definition.
/// </summary>
/// <remarks>
/// This class provides YAML mapping for various properties associated with a service in Docker Compose,
/// such as image, container configuration, environment variables, networks, and more.
/// It is designed to map directly to the service properties defined in a Docker Compose YAML file.
/// </remarks>
/// <example>
/// The <c>Service</c> class can be used to define a container's image, ports, volumes, environment settings,
/// and advanced settings like logging and health checks.
/// </example>
[AspireExport(ExposeProperties = true)]
[YamlSerializable]
public sealed class Service : NamedComposeMember
{
    /// <summary>
    /// Specifies the Docker image to be used for the service.
    /// </summary>
    /// <remarks>
    /// The image refers to the identifier of a container image hosted in a registry.
    /// This property is required if no build instructions are provided for the service.
    /// It may include an optional tag or digest to specify a particular version of the image.
    /// If omitted, Docker will default to the `latest` tag.
    /// </remarks>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>
    /// Specifies the policy for pulling the Docker image.
    /// </summary>
    /// <remarks>
    /// This property determines when the container runtime should pull the image.
    /// Common values include "always", "never", "missing", "if_not_present", or "build".
    /// If not specified, the default behavior depends on the container runtime configuration.
    /// </remarks>
    [YamlMember(Alias = "pull_policy")]
    public string? PullPolicy { get; set; }

    /// <summary>
    /// Specifies the name of the container to be used.
    /// This property maps to the "container_name" field in a Docker Compose file.
    /// If set, the container will have the specified name; otherwise, a name
    /// will be automatically generated.
    /// </summary>
    [YamlMember(Alias = "container_name")]
    public string? ContainerName { get; set; }

    /// <summary>
    /// Represents the build configuration for the service.
    /// This is used to specify the context, Dockerfile, or other related configurations
    /// required to build the Docker image for the service.
    /// </summary>
    [YamlMember(Alias = "build")]
    public Build? Build { get; set; }

    /// <summary>
    /// Represents the command to override the default command specified in the image's Dockerfile.
    /// This property allows specifying how the container should run by defining an executable and its arguments.
    /// </summary>
    [YamlMember(Alias = "command", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Command { get; set; } = [];

    /// <summary>
    /// Specifies the entrypoint to be used for the container.
    /// This property allows overriding the default entrypoint of the image
    /// and defines the executable or command that is run when the container starts.
    /// </summary>
    [YamlMember(Alias = "entrypoint", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Entrypoint { get; set; } = [];

    /// <summary>
    /// Represents a collection of environment variables for the service container.
    /// </summary>
    /// <remarks>
    /// The property allows for specifying environment variables as key-value pairs.
    /// These variables can be used to configure the behavior of the container
    /// or pass information to the application running inside the container.
    /// </remarks>
    [YamlMember(Alias = "environment", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>
    /// Represents a collection of paths to environment variable files used by the service.
    /// These files contain key-value pairs of environment variables that will be loaded
    /// and applied to the service configuration at runtime.
    /// </summary>
    [YamlMember(Alias = "env_file", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> EnvFile { get; set; } = [];

    /// <summary>
    /// Gets or sets the working directory of the container.
    /// Specifies the directory in which commands are run inside the container.
    /// Corresponds to the "working_dir" property in a Docker Compose file.
    /// </summary>
    [YamlMember(Alias = "working_dir")]
    public string? WorkingDir { get; set; }

    /// <summary>
    /// Represents a collection of port mappings for the service.
    /// Each mapping specifies how a container port is bound to a host port.
    /// </summary>
    [YamlMember(Alias = "ports", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Ports { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of ports to expose from the container without publishing them to the host machine.
    /// This property defines internal ports that the container makes available to linked services
    /// or other containers within the same network, but these ports are not accessible from outside
    /// the container’s network.
    /// </summary>
    [YamlMember(Alias = "expose", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Expose { get; set; } = [];

    /// <summary>
    /// Defines the list of volumes to be mounted into the service's container.
    /// </summary>
    /// <remarks>
    /// Volumes provide a mechanism for persisting data used by the service or for sharing data between the host and the container.
    /// Each volume entry maps a source path on the host or an anonymous volume to a target path within the container.
    /// This property can also include named volumes as defined in the Compose file's top-level `volumes` section.
    /// Volumes can specify additional options such as read-only access or volume drivers.
    /// </remarks>
    [YamlMember(Alias = "volumes", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<Volume> Volumes { get; set; } = [];

    /// <summary>
    /// Specifies a list of services that this service depends on.
    /// The dependencies are expressed as service names with optional conditions.
    /// Supported conditions are: "service_started", "service_healthy", "service_completed_successfully"
    /// This property defines the order in which services should be started,
    /// ensuring that the specified services are initialized before the current service.
    /// </summary>
    [YamlMember(Alias = "depends_on", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, ServiceDependency> DependsOn { get; set; } = [];

    /// <summary>
    /// Specifies the user that the container will run as.
    /// The value can be set to a numeric UID, a string for the username,
    /// or a combination of both (e.g., "UID:GID").
    /// </summary>
    [YamlMember(Alias = "user")]
    public string? User { get; set; }

    /// <summary>
    /// Defines the collection of networks that the service is connected to.
    /// This property specifies the names of the networks the service should be attached to.
    /// Each entry in this list represents a network defined in the Docker Compose file or an
    /// externally defined network. Connecting a service to one or more networks allows inter-service
    /// communication across those networks, as well as communication with external systems configured
    /// on those same networks.
    /// If no network is specified, the service is connected to the default network that is automatically
    /// created by Docker Compose for the project unless `network_mode` is set to another value.
    /// </summary>
    [YamlMember(Alias = "networks", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Networks { get; set; } = [];

    /// <summary>
    /// Specifies the restart policy for the container. This property determines how the
    /// container should behave in case of a crash or termination. Common values include
    /// "no", "always", "on-failure", and "unless-stopped".
    /// </summary>
    [YamlMember(Alias = "restart")]
    public string? Restart { get; set; }

    /// <summary>
    /// Represents the deployment configuration of a service in a Docker Compose configuration.
    /// </summary>
    [YamlMember(Alias = "deploy")]
    public Deploy? Deploy { get; set; }

    /// <summary>
    /// Represents the health check configuration for a service.
    /// This property specifies the parameters for evaluating the health status of a Docker service container,
    /// including commands, intervals, retries, and status conditions. It allows you to define custom health check logic
    /// to ensure the service is functioning as expected.
    /// </summary>
    [YamlMember(Alias = "healthcheck")]
    public Healthcheck? Healthcheck { get; set; }

    /// <summary>
    /// Represents the logging configuration for a service in a Docker Compose file.
    /// This property allows defining logging options such as drivers and parameters
    /// to customize how logs are handled and stored for the service.
    /// </summary>
    [YamlMember(Alias = "logging")]
    public Logging? Logging { get; set; }

    /// <summary>
    /// Represents a set of metadata labels for the service.
    /// These key-value pairs can be used to organize and identify objects within the service configuration.
    /// </summary>
    [YamlMember(Alias = "labels", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Labels { get; set; } = [];

    /// <summary>
    /// Represents the domain name of a service container.
    /// </summary>
    [YamlMember(Alias = "domainname")]
    public string? DomainName { get; set; }

    /// <summary>
    /// Gets or sets the hostname for the service container.
    /// This defines the hostname that will be assigned to the container
    /// and can be used for network identification within the container's network.
    /// </summary>
    [YamlMember(Alias = "hostname")]
    public string? Hostname { get; set; }

    /// <summary>
    /// Specifies the isolation mode for the container. This property determines
    /// the level of isolation between the container and the host system. Common
    /// values include "default", "process", or "hyperv", and the supported options
    /// may depend on the container runtime or the platform being used.
    /// </summary>
    [YamlMember(Alias = "isolation")]
    public string? Isolation { get; set; }

    /// <summary>
    /// Gets or sets the IPC (Inter-Process Communication) mode for the service.
    /// This property determines how IPC namespaces are shared between containers and the host.
    /// It can be set to values such as "none", "host", or a specific container ID to share IPC resources with.
    /// </summary>
    [YamlMember(Alias = "ipc")]
    public string? Ipc { get; set; }

    /// <summary>
    /// Specifies a custom MAC (Media Access Control) address for the container's network interface.
    /// </summary>
    /// <remarks>
    /// A MAC address is a unique identifier assigned to a network interface controller (NIC).
    /// Setting this property allows for specifying a predefined MAC address instead of letting the system assign one dynamically.
    /// This can be useful for use cases where a consistent MAC address is required, such as DHCP reservations or specific network configurations.
    /// </remarks>
    [YamlMember(Alias = "mac_address")]
    public string? MacAddress { get; set; }

    /// <summary>
    /// Gets or sets the PID (Process Identifier) namespace configuration for the container.
    /// This property determines whether the container shares the PID namespace with the host
    /// or other containers, allowing process visibility and signal sending between them.
    /// </summary>
    [YamlMember(Alias = "pid")]
    public string? Pid { get; set; }

    /// <summary>
    /// Specifies a list of Linux capabilities to add to the container.
    /// </summary>
    /// <remarks>
    /// Linux capabilities allow fine-grained control of the privileges assigned to a process.
    /// This property provides the ability to add specific capabilities to the set of capabilities available to the container.
    /// Each capability should be specified as a string in the list. Use this property to enhance the container's permissions
    /// beyond the default set provided by the Docker runtime, if required by the application running inside the container.
    /// </remarks>
    [YamlMember(Alias = "cap_add", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> CapAdd { get; set; } = [];

    /// <summary>
    /// Represents a list of Linux capabilities to be dropped from the service's container.
    /// This property can be used to restrict specific capabilities that the container
    /// should not have access to, enhancing security by implementing the principle of least privilege.
    /// </summary>
    [YamlMember(Alias = "cap_drop", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> CapDrop { get; set; } = [];

    /// <summary>
    /// Gets or sets the parent Cgroup for the container.
    /// This property defines the name of the Cgroup under which the container's resource constraints are managed.
    /// </summary>
    [YamlMember(Alias = "cgroup_parent")]
    public string? CgroupParent { get; set; }

    /// <summary>
    /// Represents a collection of device mappings for the service container.
    /// This property defines the host-to-container device paths in Docker.
    /// </summary>
    [YamlMember(Alias = "devices", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Devices { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of custom DNS server IP addresses to be used by the service container.
    /// </summary>
    [YamlMember(Alias = "dns", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Dns { get; set; } = [];

    /// <summary>
    /// Specifies the domain search options for the service's container.
    /// This property allows you to define one or more domain search suffixes
    /// that will be appended to unqualified DNS queries performed by the container.
    /// Typically used to configure how DNS resolution should behave in specific network setups.
    /// </summary>
    [YamlMember(Alias = "dns_search", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> DnsSearch { get; set; } = [];

    /// <summary>
    /// Represents additional hostname-to-IP mappings for the service.
    /// These mappings allow you to manually define hostnames and corresponding IP addresses,
    /// effectively augmenting the DNS resolution for the service's containers.
    /// </summary>
    [YamlMember(Alias = "extra_hosts", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> ExtraHosts { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of additional group IDs to add to the container's
    /// process. This allows the container to have access to resources or
    /// permissions associated with the specified groups.
    /// </summary>
    [YamlMember(Alias = "group_add", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> GroupAdd { get; set; } = [];

    /// <summary>
    /// Indicates whether the init binary should be used as the container's init process.
    /// When set to true, the init process is used to ensure proper reaping of zombie processes
    /// and signal forwarding inside the container.
    /// </summary>
    [YamlMember(Alias = "init")]
    public bool? Init { get; set; }

    /// <summary>
    /// Represents a service definition in a Docker Compose configuration file.
    /// </summary>
    [YamlMember(Alias = "links", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Links { get; set; } = [];

    /// <summary>
    /// Gets or sets the external links for the service.
    /// External links are references to services defined outside the current Docker Compose file,
    /// enabling communication with containers in other projects or environments.
    /// </summary>
    [YamlMember(Alias = "external_links", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> ExternalLinks { get; set; } = [];

    /// <summary>
    /// Specifies the network mode to be used for the container.
    /// This property determines the networking configuration, such as whether the container
    /// shares the network stack with the host, uses a predefined network, or operates in
    /// isolation. The value is typically a string that matches a network mode supported by
    /// the environment, such as 'bridge', 'host', 'none', or a custom network name.
    /// </summary>
    [YamlMember(Alias = "network_mode")]
    public string? NetworkMode { get; set; }

    /// <summary>
    /// Defines a list of profiles associated with the service.
    /// Profiles allow grouping of services and provide the ability
    /// to selectively enable services based on specified runtime
    /// profiles. If no profiles are specified, the service will be
    /// active in all configurations.
    /// </summary>
    [YamlMember(Alias = "profiles", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Profiles { get; set; } = [];

    /// <summary>
    /// Defines whether the service containers should be run in read-only mode.
    /// If set to true, the containers will have a read-only file system, limiting
    /// write operations to specific directories defined by writable mounts or tempfs.
    /// </summary>
    [YamlMember(Alias = "read_only")]
    public bool? ReadOnly { get; set; }

    /// <summary>
    /// Represents a list of security options that can be applied to the container.
    /// This is used to configure security-related settings specific to the container such as SELinux labels
    /// or AppArmor profiles, providing fine-grained control over the container's security behavior.
    /// </summary>
    [YamlMember(Alias = "security_opt", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> SecurityOpt { get; set; } = [];

    /// <summary>
    /// Represents a collection of secret references used by the service.
    /// </summary>
    /// <remarks>
    /// Each entry in the collection refers to a specific secret that is utilized by the service,
    /// typically for managing sensitive information in a secure manner (e.g., credentials, tokens).
    /// </remarks>
    [YamlMember(Alias = "secrets", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<SecretReference> Secrets { get; set; } = [];

    /// <summary>
    /// Represents a collection of configuration references associated with the service.
    /// Each configuration is defined as a reference to an external configuration resource,
    /// which can be used to manage application configurations.
    /// </summary>
    [YamlMember(Alias = "configs", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<ConfigReference> Configs { get; set; } = [];

    /// <summary>
    /// Gets or sets the stop grace period for the container.
    /// This specifies the amount of time to wait before forcing a container to stop after the stop or shutdown signal is sent.
    /// The value can be defined in a time duration format, such as "10s" for 10 seconds or "1m" for 1 minute.
    /// </summary>
    [YamlMember(Alias = "stop_grace_period")]
    public string? StopGracePeriod { get; set; }

    /// <summary>
    /// Specifies the signal that will be used to stop the container.
    /// This property allows you to define a custom stop signal other than the default SIGTERM.
    /// </summary>
    [YamlMember(Alias = "stop_signal")]
    public string? StopSignal { get; set; }

    /// <summary>
    /// Represents a set of kernel parameters, specified as key-value pairs,
    /// that can be applied to the container at runtime.
    /// This property allows customization of specific Linux kernel settings
    /// (sysctl parameters) for the container, enabling fine-tuned control
    /// over its behavior. Common use cases include tuning network parameters
    /// or configuring shared memory limits.
    /// Note: Supported kernel parameters will vary based on the Docker daemon
    /// and the host system. Unsupported parameters will result in an error.
    /// Example: Use this property to set parameters like `net.ipv4.tcp_syncookies`
    /// or `net.core.somaxconn`.
    /// </summary>
    [YamlMember(Alias = "sysctls", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Sysctls { get; set; } = [];

    /// <summary>
    /// Specifies a list of temporary file systems (tmpfs) to be mounted inside the container.
    /// Each entry represents a directory on the container's filesystem, mounted as a tmpfs,
    /// which resides in-memory and is typically used for ephemeral storage or caching purposes.
    /// </summary>
    [YamlMember(Alias = "tmpfs", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Tmpfs { get; set; } = [];

    /// <summary>
    /// Indicates whether standard input (stdin) should remain open and be attached
    /// to the service container, even if no terminal is connected.
    /// </summary>
    [YamlMember(Alias = "stdin_open")]
    public bool? StdinOpen { get; set; }

    /// <summary>
    /// Specifies whether a pseudo-TTY (teletypewriter) should be allocated for the container.
    /// When set to true, it enables the container to run with an interactive terminal session.
    /// </summary>
    [YamlMember(Alias = "tty")]
    public bool? Tty { get; set; }

    /// <summary>
    /// Represents a collection of ulimit constraints for the service.
    /// Ulimits specify system resource limitations to be applied to the container,
    /// such as maximum number of open files or maximum stack size.
    /// </summary>
    [YamlMember(Alias = "ulimits", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, Ulimit> Ulimits { get; set; } = [];

    /// <summary>
    /// Adds a volume to the service's list of volumes. If the volumes collection is
    /// null, it initializes a new collection before adding the volume.
    /// </summary>
    /// <param name="volume">The volume to be added to the service.</param>
    /// <returns>The updated <see cref="Service"/> instance with the added volume.</returns>
    public Service AddVolume(Volume volume)
    {
        Volumes.Add(volume);
        return this;
    }

    /// <summary>
    /// Adds multiple volumes to the service's list of volumes. If the volumes collection is empty,
    /// the provided volumes will be appended to the existing collection.
    /// </summary>
    /// <param name="volumes">A collection of volumes to be added to the service.</param>
    /// <returns>The updated <see cref="Service"/> instance with the added volumes.</returns>
    public Service AddVolumes(IEnumerable<Volume> volumes)
    {
        Volumes.AddRange(volumes);
        return this;
    }

    /// <summary>
    /// Adds an environmental variable to the service's environment dictionary.
    /// If the specified value is null, it assigns an empty string as the value.
    /// </summary>
    /// <param name="key">The key for the environmental variable.</param>
    /// <param name="value">The value of the environmental variable. If null, an empty string will be used.</param>
    /// <returns>The updated <see cref="Service"/> instance with the added environmental variable.</returns>
    public Service AddEnvironmentalVariable(string key, string? value)
    {
        Environment[key] = value ?? string.Empty;

        return this;
    }

    /// <summary>
    /// Adds a configuration reference to the service's list of configurations.
    /// This method allows you to include external configuration resources
    /// that the service can utilize at runtime.
    /// </summary>
    /// <param name="config">The config reference to add</param>
    /// <returns>The updated <see cref="Service"/> instance with the added environmental variable.</returns>
    public Service AddConfig(ConfigReference config)
    {
        Configs.Add(config);
        return this;
    }
}
