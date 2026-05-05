// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class ContainerSpec
{
    // Container name displayed in Docker. If not specified, the metadata name + random suffix is used.
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    // Image to be used to create the container
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    // Optional configuration to build an image from a Dockerfile instead of using a pre-built image
    [JsonPropertyName("build")]
    public BuildContext? Build { get; set; }

    // Volumes that should be mounted into the container
    [JsonPropertyName("volumeMounts")]
    public List<VolumeMount>? VolumeMounts { get; set; }

    // Exposed ports
    [JsonPropertyName("ports")]
    public List<ContainerPortSpec>? Ports { get; set; }

    // Environment variables to be used for the container
    [JsonPropertyName("env")]
    public List<EnvVar>? Env { get; set; }

    // Environment files to use to populate Container environment during startup
    [JsonPropertyName("envFiles")]
    public List<string>? EnvFiles { get; set; }

    // Container restart policy
    [JsonPropertyName("restartPolicy")]
    public string? RestartPolicy { get; set; } = ContainerRestartPolicy.None;

    // Command to run in the container (entrypoint)
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    // Arguments to pass to the command that starts the container
    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    // Optional labels to apply to the container instance
    [JsonPropertyName("labels")]
    public List<ContainerLabel>? Labels { get; set; }

    // Additional arguments to pass to the container run command
    [JsonPropertyName("runArgs")]
    public List<string>? RunArgs { get; set; }

    // Should this container be created and persisted between DCP runs?
    [JsonPropertyName("persistent")]
    public bool? Persistent { get; set; }

    [JsonPropertyName("networks")]
    public List<ContainerNetworkConnection>? Networks { get; set; }

    /// <summary>
    /// Should this resource be started? If set to false, we will not attempt
    /// to start the resource until Start is set to true (or null).
    /// </summary>
    [JsonPropertyName("start")]
    public bool? Start { get; set; }

    // Should this resource be stopped?
    [JsonPropertyName("stop")]
    public bool? Stop { get; set; }

    /// <summary>
    /// Optional lifecycle key for the resource (used to identify changes to persistent resources requiring a restart).
    /// If unset, DCP will calculate a default lifecycle key based on a hash of various resource spec properties.
    /// </summary>
    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    /// <summary>
    /// Optional pull policy for the container image.
    /// </summary>
    [JsonPropertyName("pullPolicy")]
    public string? PullPolicy { get; set; }

    [JsonPropertyName("createFiles")]
    public List<ContainerCreateFileSystem>? CreateFiles { get; set; }

    // List of public PEM certificates to be trusted by the container
    [JsonPropertyName("pemCertificates")]
    public ContainerPemCertificates? PemCertificates { get; set; }
}

internal sealed class BuildContext
{
    // The path to the directory that will serve as the root of the image build context
    [JsonPropertyName("context")]
    public string? Context { get; set; }

    // Optional path to a specific Dockerfile to use in the build (defaults to looking for a Dockerfile in the root Context folder)
    [JsonPropertyName("dockerfile")]
    public string? Dockerfile { get; set;}

    // Optional build --build-args to pass to the build command
    [JsonPropertyName("args")]
    public List<EnvVar>? Args { get; set; }

    // Optional build secret mounts to pass to the build command
    [JsonPropertyName("secrets")]
    public List<BuildContextSecret>? Secrets { get; set; }

    // Optional specific stage to use when building a multiple stage Dockerfile
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    // Optional additional tags to apply to the built image
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // Optional labels to apply to the built image
    [JsonPropertyName("labels")]
    public List<ContainerLabel>? Labels { get; set; }

    // Optional target platform for the build (e.g. "linux/amd64")
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}

internal sealed class BuildContextSecret
{
    // The ID of the secret (a secret can be used in a Dockerfile with `RUN --mount-type=secret,id=<id>,target=<targetpath>`)
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    // Type of the secret, can be "env" or "file".
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // Value of the secret to be used in the build when the type of the secret is "env".
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    // Path to secret file/folder that will be mounted as a build secret using --secret
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

internal static class VolumeMountType
{
    // A volume mount to a host directory
    public const string Bind = "bind";

    // A volume mount to a volume managed by the container orchestrator
    public const string Volume = "volume";
}

internal sealed class VolumeMount
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = VolumeMountType.Bind;

    // Bind mounts: the host directory to mount
    // Volume mounts: name of the volume to mount
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    // The path within the container that the mount will use
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    // True if the mounted file system is supposed to be read-only
    [JsonPropertyName("readOnly")]
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// Health probes to be run for the container.
    /// </summary>
    [JsonPropertyName("healthProbes")]
    public List<HealthProbe>? HealthProbes { get; set; }
}

internal sealed class ContainerNetworkConnection
{
    // DCP Resource name of a ContainerNetwork to connect to
    // A container won't start running until it can be connected to all specified networks
    [JsonPropertyName("name")]
	public string? Name { get; set; }

	// Aliases of the container on the network
	// This enables container DNS resolution
    [JsonPropertyName("aliases")]
	public List<string>? Aliases { get; set; }
}

internal sealed class ContainerLabel
{
    // The label key
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    // The label value
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal static class ContainerRestartPolicy
{
    // Do not automatically restart the container when it exits (default)
    public const string None = "no";

    // Restart only if the container exits with non-zero status
    public const string OnFailure = "on-failure";

    // Restart container, except if container is explicitly stopped (or container daemon is stopped/restarted)
    public const string UnlessStopped = "unless-stopped";

    // Always try to restart the container
    public const string Always = "always";
}

internal static class ContainerPullPolicy
{
    // Always attempt to pull a newer image from the registry
    public const string Always = "always";

    // Only pull the image if there isn't a version already available locally (this may mean the image is out of date)
    public const string Missing = "missing";

    // Never pull the image from the registry even if it is missing locally
    public const string Never = "never";
}

internal static class PortProtocol
{
    public const string TCP = "TCP";

    public const string UDP = "UDP";

    public static string Canonicalize(string protocol)
    {
        var protocolUC = protocol.ToUpperInvariant();
        switch (protocolUC)
        {
            case TCP:
            case UDP:
                return protocolUC;
            default:
                throw new ArgumentException("Port protocol value must be 'TCP' or 'UDP'");
        }
    }

    public static ProtocolType ToProtocolType(string protocol)
    {
        var canonical = Canonicalize(protocol);
        switch (canonical)
        {
            case TCP:
                return ProtocolType.Tcp;
            case UDP:
                return ProtocolType.Udp;
            default:
                throw new ArgumentException("Supported protocols are TCP and UDP");
        }
    }

    public static string FromProtocolType(ProtocolType protocolType)
    {
        switch (protocolType)
        {
            case ProtocolType.Tcp:
                return TCP;
            case ProtocolType.Udp:
                return UDP;
            default:
                throw new ArgumentException("Supported protocols are TCP and UDP");
        }
    }
}

internal sealed class ContainerPortSpec
{
    // Optional: If specified, this must be a valid port number, 0 < x < 65536.
    [JsonPropertyName("hostPort")]
    public int? HostPort { get; set; }

    // Required: This must be a valid port number, 0 < x < 65536.
    [JsonPropertyName("containerPort")]
    public int? ContainerPort { get; set; }

    // The network protocol to be used, defaults to TCP
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = PortProtocol.TCP;

    // Optional: What host IP to bind the external port to.
    [JsonPropertyName("hostIP")]
    public string? HostIP { get; set; }
}

internal sealed class ContainerCreateFileSystem : IEquatable<ContainerCreateFileSystem>
{
    // The (absolute) base path to create the child file system entries in the container.
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    // The default owner UID to use for created (or updated) file system entries. Defaults to 0 for root.
    [JsonPropertyName("defaultOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefaultOwner { get; set; }

    // The default group GID to use for created (or updated) file system entries. Defaults to 0 for root.
    [JsonPropertyName("defaultGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefaultGroup { get; set; }

    // The umask for created files and folders without explicit permissions set (defaults to 022 if null)
    [JsonPropertyName("umask")]
    public int? Umask { get; set; }

    // The list of file system entries to create (or update) in the container.
    [JsonPropertyName("entries")]
    public List<ContainerFileSystemEntry>? Entries { get; set; }

    public bool Equals(ContainerCreateFileSystem? other)
    {
        if (other is null)
        {
            return false;
        }

        return Destination == other.Destination
            && DefaultOwner == other.DefaultOwner
            && DefaultGroup == other.DefaultGroup
            && Umask == other.Umask
            && (Entries ?? Enumerable.Empty<ContainerFileSystemEntry>()).SequenceEqual(other.Entries ?? Enumerable.Empty<ContainerFileSystemEntry>());
    }
}

internal static class ContainerFileSystemItemExtensions
{
    public static ContainerFileSystemEntry ToContainerFileSystemEntry(this ContainerFileSystemItem item)
    {
        var type = item switch
        {
            ContainerFile => ContainerFileSystemEntryType.File,
            ContainerOpenSSLCertificateFile => ContainerFileSystemEntryType.OpenSSL,
            ContainerDirectory => ContainerFileSystemEntryType.Directory,
            _ => throw new ArgumentException("Unknown file system entry type")
        };

        var entry = new ContainerFileSystemEntry
        {
            Type = type,
            Name = item.Name,
            Owner = item.Owner,
            Group = item.Group,
            Mode = (int)item.Mode,
        };

        if (item is ContainerFileBase file)
        {
            entry.Source = file.SourcePath;
            entry.Contents = file.Contents;
            entry.ContinueOnError = file.ContinueOnError;

            if (file.Contents is not null && file.SourcePath is not null)
            {
                throw new ArgumentException("Both SourcePath and Contents are set for a file entry");
            }
        }
        else if (item is ContainerDirectory directory)
        {
            entry.Entries = directory.Entries?.Select(e => e.ToContainerFileSystemEntry()).ToList();
        }

        return entry;
    }
}

internal sealed class ContainerFileSystemEntry : IEquatable<ContainerFileSystemEntry>
{
    // The type of the file system entry (file or directory)
    [JsonPropertyName("type")]
    public string Type { get; set; } = ContainerFileSystemEntryType.File;

    // The name of the file system entry
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // The UID of the user that owns the file system (if null, the owner will be inherited from its parent directory or the request default)
    [JsonPropertyName("owner")]
    public int? Owner { get; set; }

    // The GID of the group that owns the file system (if null, the group will be inherited from its parent directory or the request default)
    [JsonPropertyName("group")]
    public int? Group { get; set; }

    // The file system mode (permissions) of the file system entry (if 0, the mode will be inherited from its parent directory or the request default)
    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    // If the file system entry is a file, this is the optional path to a file on the host to use as the contents of that file.
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    // If the file system entry is a file, this is the contents of that file. Setting Contents for a directory is an error. Contents, RawContents, and Source are mutually exclusive.
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }

    // If the file system entry is a file, this is the base64-encoded raw contents of the file system entry. Setting RawContents for a directory is an error. RawContents, Contents, and Source are mutually exclusive.
    [JsonPropertyName("rawContents")]
    public string? RawContents { get; set; }

    // If the file system entry is a directory, this is the list of entries in that directory. Setting Entries for a file is an error.
    [JsonPropertyName("entries")]
    public List<ContainerFileSystemEntry>? Entries { get; set; }

    // If true, errors creating this entry will be ignored (does not apply to directory entries)
    [JsonPropertyName("continueOnError")]
    public bool? ContinueOnError { get; set; }

    public bool Equals(ContainerFileSystemEntry? other)
    {
        if (other is null)
        {
            return false;
        }

        return Type == other.Type
            && Name == other.Name
            && Owner == other.Owner
            && Group == other.Group
            && Mode == other.Mode
            && Source == other.Source
            && Contents == other.Contents
            && (Entries ?? Enumerable.Empty<ContainerFileSystemEntry>()).SequenceEqual(other.Entries ?? Enumerable.Empty<ContainerFileSystemEntry>());
    }
}

internal static class ContainerFileSystemEntryType
{
    public const string Directory = "directory";

    public const string File = "file";

    public const string OpenSSL = "openssl";
}

internal sealed class ContainerPemCertificates
{
    // The destination in the container the certificates should be written to
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    // The list of PEM encoded certificates to write
    [JsonPropertyName("certificates")]
    public List<PemCertificate>? Certificates { get; set; }

    // Optional list of bundle paths to overwrite in the container with the generated CA bundle
    [JsonPropertyName("overwriteBundlePaths")]
    public List<string>? OverwriteBundlePaths { get; set; }

    // Should resource creation continue if there are errors writing one or more certificates?
    [JsonPropertyName("continueOnError")]
    public bool ContinueOnError { get; set; }
}

internal sealed record ContainerStatus : V1Status
{
    // Container name displayed in Docker
    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    // Current state of the Container.
    [JsonPropertyName("state")]
    public string? State { get; set; }

    // ID of the Container (if an attempt to start the Container was made)
    [JsonPropertyName("containerId")]
    public string? ContainerId { get; set; }

    // Timestamp of the Container start attempt
    [JsonPropertyName("startupTimestamp")]
    public DateTime? StartupTimestamp { get; set; }

    // Timestamp when the Container was terminated last
    [JsonPropertyName("finishTimestamp")]
    public DateTime? FinishTimestamp { get; set; }

    // Exit code of the Container.
    // Default is -1, meaning the exit code is not known, or the container is still running.
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; } = Conventions.UnknownExitCode;

    // Effective values of environment variables, after all substitutions have been applied
    [JsonPropertyName("effectiveEnv")]
    public List<EnvVar>? EffectiveEnv { get; set; }

    // Effective values of launch arguments to be passed to the Container, after all substitutions are applied.
    [JsonPropertyName("effectiveArgs")]
    public List<string>? EffectiveArgs { get; set; }

    // Any ContainerNetworks this container is attached to
    [JsonPropertyName("networks")]
    public List<string>? Networks { get; set; }

    /// <summary>
    /// The health status of the container <see cref="HealthStatus"/> for allowed values.
    /// </summary>
    [JsonPropertyName("healthStatus")]
    public string? HealthStatus { get; set; }

    /// <summary>
    /// Latest results for health probes configured for the container.
    /// </summary>
    [JsonPropertyName("healthProbeResults")]
    public List<HealthProbeResult>? HealthProbeResults { get; set;}

    /// <summary>
    /// The lifecycle key for the resource (used to identify changes to persistent resources requiring a restart).
    /// </summary>
    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    // Note: the ContainerStatus has "Message" property that represents a human-readable information about Container state.
    // It is provided by V1Status base class.
}

internal static class ContainerState
{
    // Pending is the initial Container state. No attempt has been made to run the container yet.
    public const string Pending = "Pending";

    // Building indicates an image is being built from a Dockerfile, but a container hasn't been created yet.
    public const string Building = "Building";

    // Starting indicates a container is in the process of starting (pulling images, waiting to join to initial networks, etc.)
    public const string Starting = "Starting";

    // A start attempt was made, but it failed
    public const string FailedToStart = "FailedToStart";

    // Container has been started and is executing
    public const string Running = "Running";

    // Container is paused
    public const string Paused = "Paused";

    // Container finished execution
    public const string Exited = "Exited";

    // Container is in the process of stopping (waiting for container processes to exit, etc.).
    public const string Stopping = "Stopping";

    // Unknown means for some reason container state is unavailable.
    public const string Unknown = "Unknown";

    // Indicates that the container start is blocked because the container runtime isn't healthy.
    // Startup will resume once the runtime has recovered.
    public const string RuntimeUnhealthy = "RuntimeUnhealthy";
}

internal sealed class Container : CustomResource<ContainerSpec, ContainerStatus>, IKubernetesStaticMetadata
{
    [JsonConstructor]
    public Container(ContainerSpec spec) : base(spec) { }

    public static Container Create(string name, string image)
    {
        var c = new Container(new ContainerSpec { Image = image });

        c.Kind = Dcp.ContainerKind;
        c.ApiVersion = Dcp.GroupVersion.ToString();
        c.Metadata.Name = name;
        c.Metadata.NamespaceProperty = string.Empty;

        return c;
    }

    public bool LogsAvailable => !string.IsNullOrEmpty(this.Status?.State);

    public static string ObjectKind => Dcp.ContainerKind;
}

