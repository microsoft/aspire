// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class EndpointSpec
{
    // Namespace of the service the endpoint implements
    [JsonPropertyName("serviceNamespace")]
    public string? ServiceNamespace { get; set; }

    // Name of the service the endpoint implements
    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    // The address of the endpoint
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    // The port of the endpoint
    [JsonPropertyName("port")]
    public int? Port { get; set; }
}

internal sealed record EndpointStatus : V1Status
{
    // Currently Endpoint has no status properties, but that may change in future.
}

internal sealed class Endpoint : CustomResource<EndpointSpec, EndpointStatus>, IKubernetesStaticMetadata
{
    [JsonConstructor]
    public Endpoint(EndpointSpec spec) : base(spec) { }

    public static Endpoint Create(string name, string serviceNamespace, string serviceName)
    {
        var e = new Endpoint(new EndpointSpec
        {
            ServiceName = serviceName,
            ServiceNamespace = serviceNamespace,
        });

        e.Kind = Dcp.EndpointKind;
        e.ApiVersion = Dcp.GroupVersion.ToString();
        e.Metadata.Name = name;
        e.Metadata.NamespaceProperty = string.Empty;

        return e;
    }

    public static string ObjectKind => Dcp.EndpointKind;
}

