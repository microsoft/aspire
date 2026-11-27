// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal class LogicalServiceSpec
{
    // DCP defaults omitted protocol and allocation mode to TCP and Localhost.
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Port { get; set; }

    [JsonPropertyName("addressAllocationMode")]
    public string? AddressAllocationMode { get; set; }
}

internal sealed class LogicalServiceStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("effectiveAddresses")]
    public List<ServiceAddress>? EffectiveAddresses { get; set; }

    [JsonPropertyName("proxylessEndpointRef")]
    public string? ProxylessEndpointRef { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class LogicalServicePhase
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
    public const string Unknown = "Unknown";
    public const string Failed = "Failed";
}

internal static class ServiceAddressAllocationMode
{
    public const string Localhost = "Localhost";
    public const string IPv4ZeroOne = "IPv4ZeroOne";
    public const string IPv4Loopback = "IPv4Loopback";
    public const string IPv6ZeroOne = "IPv6ZeroOne";
    public const string AllInterfaces = "AllInterfaces";
    public const string IPv4AllInterfaces = "IPv4AllInterfaces";
    public const string IPv6AllInterfaces = "IPv6AllInterfaces";
    public const string Proxyless = "Proxyless";
}

internal sealed class LogicalService(LogicalServiceSpec spec) : CustomResource<LogicalServiceSpec, LogicalServiceStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "LogicalService";

    public static LogicalService Create(string name, string namespaceName)
    {
        return new(new())
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}

internal sealed class ServiceAddress
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

internal sealed class ServiceEndpointSpec
{
    [JsonPropertyName("serviceRef")]
    public string ServiceRef { get; set; } = "";

    [JsonPropertyName("addresses")]
    public List<ServiceAddress> Addresses { get; set; } = [];
}

internal sealed class ServiceEndpoint(ServiceEndpointSpec spec) : CustomResource, IKubernetesStaticMetadata
{
    public static string ObjectKind => "ServiceEndpoint";

    [JsonPropertyName("spec")]
    public ServiceEndpointSpec Spec { get; set; } = spec;

    public static ServiceEndpoint Create(string name, string namespaceName, string serviceRef)
    {
        return new(new() { ServiceRef = serviceRef })
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
