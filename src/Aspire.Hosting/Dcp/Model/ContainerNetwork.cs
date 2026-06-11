// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class ContainerNetworkSpec
{
    // Name of the network (if omitted, a name is generated based on the resource name)
    [JsonPropertyName("networkName")]
    public string? NetworkName { get; set; }

    // Should IPv6 be enabled for the network?
    [JsonPropertyName("ipv6")]
    public bool? IPV6 { get; set; }

    // Should this network be created and persisted between DCP runs?
    [JsonPropertyName("persistent")]
    public bool? Persistent { get; set; }
}

internal sealed record ContainerNetworkStatus : V1Status
{
    // The current state of the network
    [JsonPropertyName("state")]
    public string? State { get; set; }

    // The ID of the network
    [JsonPropertyName("id")]
    public string? ID { get; set; }

    // The name of the network
    [JsonPropertyName("networkName")]
    public string? NetworkName { get; set; }

    // The driver of the network
    [JsonPropertyName("driver")]
    public string? Driver { get; set; }

    // Does the network support IPv6?
    [JsonPropertyName("ipv6")]
    public bool? IPv6 { get; set; }

    // Subnets allocated to the network (if any)
    [JsonPropertyName("subnets")]
    public List<string>? Subnets { get; set; }

    // Gateways allocated to the network (if any)
    [JsonPropertyName("gateways")]
    public List<string>? Gateways { get; set; }

    // The list of container IDs connected to the network
    [JsonPropertyName("containerIds")]
    public List<string>? ContainerIDs { get; set; }

    // Note: the ContainerNetworkStatus has "Message" property that represents a human-readable information about Network state.
    // It is provided by V1Status base class.
}

internal static class ContainerNetworkState
{
    // The network is being created, this is the initial state
    public const string ContainerNetworkStatePending = "Pending";

    // The network was successfully created
    public const string ContainerNetworkStateRunning = "Running";

    // An attempt was made to create the network, but it failed
    public const string ContainerNetworkStateFailedToStart = "FailedToStart";

    // Network was running at some point, but has been removed
    public const string ContainerNetworkStateRemoved = "Removed";

    // An existing network was not found
    public const string ContainerNetworkStateNotFound = "NotFound";
}

internal sealed class ContainerNetwork : CustomResource<ContainerNetworkSpec, ContainerNetworkStatus>, IKubernetesStaticMetadata
{
    [JsonConstructor]
    public ContainerNetwork(ContainerNetworkSpec spec) : base(spec) { }

    public static ContainerNetwork Create(string name, bool useIpV6 = false)
    {
        var c = new ContainerNetwork(new ContainerNetworkSpec { IPV6 = useIpV6 });

        c.Kind = Dcp.ContainerNetworkKind;
        c.ApiVersion = Dcp.GroupVersion.ToString();
        c.Metadata.Name = name;
        c.Metadata.NamespaceProperty = string.Empty;

        return c;
    }

    public static string ObjectKind => Dcp.ContainerNetworkKind;
}
