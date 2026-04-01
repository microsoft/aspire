// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

/// <summary>
/// Defines a single tunnel for communications between container clients and services on the host.
/// </summary>
internal sealed class TunnelConfiguration
{
    /// <summary>
    /// User-friendly name for the tunnel (used in status reporting and debugging).
    /// Must be unique within the ContainerNetworkTunnelProxy and cannot be empty.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Namespace of the Service that identifies the server the tunnel connects to.
    /// </summary>
    [JsonPropertyName("serverServiceNamespace")]
    public string? ServerServiceNamespace { get; set; }

    /// <summary>
    /// Name of the Service that identifies the server the tunnel connects to.
    /// </summary>
    [JsonPropertyName("serverServiceName")]
    public string? ServerServiceName { get; set; }

    /// <summary>
    /// Gets or sets the namespace of the Service associated with the client proxy on the container network.
    /// </summary>
    [JsonPropertyName("clientServiceNamespace")]
    public string? ClientServiceNamespace { get; set; }

    /// <summary>
    /// Gets or sets the name of the Service associated with the client proxy on the container network.
    /// </summary>
    [JsonPropertyName("clientServiceName")]
    public string? ClientServiceName { get; set; }
}

internal sealed class ContainerNetworkTunnelProxySpec
{
    /// <summary>
    /// Reference to the ContainerNetwork that the client proxy should connect to.
    /// This field is required and must reference an existing ContainerNetwork resource.
    /// </summary>
    [JsonPropertyName("containerNetworkName")]
    public string? ContainerNetworkName { get; set; }

    /// <summary>
    /// Aliases (DNS names) that can be used to reach the client proxy container on the container network.
    /// </summary>
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    /// <summary>
    /// List of tunnels to prepare. Each tunnel enables clients on the container network
    /// to connect to a server on the host (establish a tunnel stream).
    /// </summary>
    [JsonPropertyName("tunnels")]
    public List<TunnelConfiguration>? Tunnels { get; set; }

    /// <summary>
    /// Base container image to use for the client proxy container.
    /// Defaults to mcr.microsoft.com/azurelinux/base/core:3.0 if not specified.
    /// </summary>
    [JsonPropertyName("baseImage")]
    public string? BaseImage { get; set; }
}

internal static class TunnelState
{
    /// <summary>
    /// Tunnel is ready and accepting connections.
    /// </summary>
    public const string Ready = "Ready";

    /// <summary>
    /// Tunnel preparation failed, see <see cref="TunnelStatus.ErrorMessage"/> for details.
    /// </summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Initial state -- no attempt to prepare the tunnel have been made yet.
    /// </summary>
    public const string Empty = "";

    /// <summary>
    /// Tunnel is being prepared, or is waiting for required services to become ready.
    /// </summary>
    public const string NotReady = "NotReady";
}

/// <summary>
/// Represents the status of a single tunnel managed by <see cref="ContainerNetworkTunnelProxy"/>.
/// </summary>
internal sealed class TunnelStatus
{
    /// <summary>
    /// Name of the tunnel (matches <see cref="TunnelConfiguration.Name"/>).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Internal tunnel ID assigned by the proxy pair.
    /// </summary>
    [JsonPropertyName("tunnelId")]
    public uint TunnelId { get; set; }

    /// <summary>
    /// Current state of the tunnel. <seealso cref="TunnelState"/>
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = TunnelState.Empty;

    /// <summary>
    /// Human-readable explanation for why the tunnel preparation failed (if it did).
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Addresses on the container network that client proxy is listening on for this tunnel.
    /// May be empty if the tunnel is not ready.
    /// </summary>
    [JsonPropertyName("clientProxyAddresses")]
    public List<string>? ClientProxyAddresses { get; set; }

    /// <summary>
    /// Port on the container network that client proxy is listening on for this tunnel.
    /// May be zero if the tunnel is not ready.
    /// </summary>
    [JsonPropertyName("clientProxyPort")]
    public int ClientProxyPort { get; set; }

    /// <summary>
    /// The timestamp for the status (last update).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }
}

internal static class ContainerNetworkTunnelProxyState
{
    /// <summary>
    /// Equivalent to <see cref="Pending"/>.
    /// May be encountered when ContainerNetworkTunnelProxy status has not been initialized yet.
    /// </summary>
    public const string Empty = "";

    /// <summary>
    /// Initial state - proxy pair is being created.
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>
    /// Building the client proxy container image.
    /// </summary>
    public const string BuildingImage = "BuildingImage";

    /// <summary>
    /// Starting the proxy pair.
    /// </summary>
    public const string Starting = "Starting";

    /// <summary>
    /// Proxy pair is ready with all tunnels operational.
    /// </summary>
    public const string Running = "Running";

    /// <summary>
    /// Proxy pair encountered an unrecoverable error, either during startup, or during execution.
    /// </summary>
    public const string Failed = "Failed";
}

internal sealed record ContainerNetworkTunnelProxyStatus : V1Status
{
    /// <summary>
    /// Current overall state of the tunnel proxy. <seealso cref="ContainerNetworkTunnelProxyState"/>
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = ContainerNetworkTunnelProxyState.Empty;

    /// <summary>
    /// Status of individual tunnels within the proxy pair.
    /// </summary>
    [JsonPropertyName("tunnelStatuses")]
    public List<TunnelStatus>? TunnelStatuses { get; set; }

    /// <summary>
    /// Monotonically increasing version number of the tunnel configuration that was applied to the proxy pair.
    /// Can be used by clients changing tunnel configuration (<see cref="ContainerNetworkTunnelProxySpec.Tunnels"/>)
    /// to learn that the new configuration has become effective.
    /// </summary>
    [JsonPropertyName("tunnelConfigurationVersion")]
    public int TunnelConfigurationVersion { get; set; }

    /// <summary>
    /// The name and tag of the container image used for the client proxy container.
    /// </summary>
    [JsonPropertyName("clientProxyContainerImage")]
    public string? ClientProxyContainerImage { get; set; }

    /// <summary>
    /// Container ID of the running client proxy container.
    /// </summary>
    [JsonPropertyName("clientProxyContainerId")]
    public string? ClientProxyContainerId { get; set; }

    /// <summary>
    /// Server proxy process ID.
    /// </summary>
    [JsonPropertyName("serverProxyProcessId")]
    public long? ServerProxyProcessId { get; set; }

    /// <summary>
    /// Server proxy process startup timestamp.
    /// </summary>
    [JsonPropertyName("serverProxyStartupTimestamp")]
    public DateTime? ServerProxyStartupTimestamp { get; set; }

    /// <summary>
    /// The path of a temporary file that contains captured standard output data from the server proxy process.
    /// </summary>
    [JsonPropertyName("serverProxyStdOutFile")]
    public string? ServerProxyStdOutFile { get; set; }

    /// <summary>
    /// The path of a temporary file that contains captured standard error data from the server proxy process.
    /// </summary>
    [JsonPropertyName("serverProxyStdErrFile")]
    public string? ServerProxyStdErrFile { get; set; }

    /// <summary>
    /// Published (host) port for client proxy control endpoint.
    /// </summary>
    [JsonPropertyName("clientProxyControlPort")]
    public int ClientProxyControlPort { get; set; }

    /// <summary>
    /// Published (host) port for client proxy data endpoint.
    /// </summary>
    [JsonPropertyName("clientProxyDataPort")]
    public int ClientProxyDataPort { get; set; }

    /// <summary>
    /// Server proxy control port (for controlling the proxy pair).
    /// </summary>
    [JsonPropertyName("serverProxyControlPort")]
    public int ServerProxyControlPort { get; set; }
}

internal sealed class ContainerNetworkTunnelProxy : CustomResource<ContainerNetworkTunnelProxySpec, ContainerNetworkTunnelProxyStatus>, IKubernetesStaticMetadata
{
    /// <summary>
    /// Contains updated tunnel configurations that have not yet been applied to the proxy pair.
    /// </summary>
    [JsonIgnore]
    public List<TunnelConfiguration> UpdatedTunnels { get; private set; }

    [JsonConstructor]
    public ContainerNetworkTunnelProxy(ContainerNetworkTunnelProxySpec spec) : base(spec)
    {
        UpdatedTunnels = spec.Tunnels ?? new List<TunnelConfiguration>();
    }

    public static ContainerNetworkTunnelProxy Create(string name)
    {
        var tp = new ContainerNetworkTunnelProxy(new ContainerNetworkTunnelProxySpec());

        tp.Kind = Dcp.ContainerNetworkTunnelProxyKind;
        tp.ApiVersion = Dcp.GroupVersion.ToString();
        tp.Metadata.Name = name;
        tp.Metadata.NamespaceProperty = string.Empty;

        return tp;
    }

    public static string ObjectKind => Dcp.ContainerNetworkTunnelProxyKind;
}
