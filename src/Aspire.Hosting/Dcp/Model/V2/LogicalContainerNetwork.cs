// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class LogicalContainerNetworkSpec
{
    [JsonPropertyName("networkName")]
    public string? NetworkName { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("network")]
    public LogicalContainerNetworkConfig? Network { get; set; }
}

internal sealed class LogicalContainerNetworkConfig
{
    [JsonPropertyName("ipv6")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IPv6 { get; set; }

    [JsonPropertyName("labels")]
    public List<Label>? Labels { get; set; }
}

internal sealed class LogicalContainerNetworkStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("physicalNetworkRef")]
    public string? PhysicalNetworkRef { get; set; }

    [JsonPropertyName("lifecycleKey")]
    public string? LifecycleKey { get; set; }

    [JsonPropertyName("networkID")]
    public string? NetworkId { get; set; }

    [JsonPropertyName("networkName")]
    public string? NetworkName { get; set; }

    [JsonPropertyName("driver")]
    public string? Driver { get; set; }

    [JsonPropertyName("ipv6")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IPv6 { get; set; }

    [JsonPropertyName("subnets")]
    public List<string>? Subnets { get; set; }

    [JsonPropertyName("gateways")]
    public List<string>? Gateways { get; set; }

    [JsonPropertyName("containerIDs")]
    public List<string>? ContainerIds { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class LogicalContainerNetworkMode
{
    public const string Session = "session";
    public const string Persistent = "persistent";
    public const string Existing = "existing";
}

internal static class LogicalContainerNetworkPhase
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
    public const string Unknown = "Unknown";
    public const string Failed = "Failed";
}

internal sealed class LogicalContainerNetwork(LogicalContainerNetworkSpec spec) : CustomResource<LogicalContainerNetworkSpec, LogicalContainerNetworkStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "LogicalContainerNetwork";

    public static LogicalContainerNetwork Create(string name, string namespaceName)
    {
        return new(new())
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
