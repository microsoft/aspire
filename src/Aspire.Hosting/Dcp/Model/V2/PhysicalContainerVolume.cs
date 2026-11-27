// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model.V2;

internal sealed class PhysicalContainerVolumeSpec
{
    [JsonPropertyName("volumeName")]
    public string VolumeName { get; set; } = "";

    [JsonPropertyName("removeRuntimeVolumeOnDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RemoveRuntimeVolumeOnDelete { get; set; }

    [JsonPropertyName("labels")]
    public List<Label>? Labels { get; set; }
}

internal sealed class PhysicalContainerVolumeStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("volumeID")]
    public string? VolumeId { get; set; }

    [JsonPropertyName("driver")]
    public string? Driver { get; set; }

    [JsonPropertyName("mountPoint")]
    public string? MountPoint { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonConverter(typeof(KubernetesMicroTimeJsonConverter))]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("conditions")]
    public List<V1Condition>? Conditions { get; set; }
}

internal static class PhysicalContainerVolumePhase
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";
    public const string Unknown = "Unknown";
    public const string Failed = "Failed";
}

internal sealed class PhysicalContainerVolume(PhysicalContainerVolumeSpec spec) : CustomResource<PhysicalContainerVolumeSpec, PhysicalContainerVolumeStatus>(spec), IKubernetesStaticMetadata
{
    public static string ObjectKind => "PhysicalContainerVolume";

    public static PhysicalContainerVolume Create(string name, string namespaceName, string volumeName)
    {
        return new(new() { VolumeName = volumeName })
        {
            Kind = ObjectKind,
            ApiVersion = Dcp.GroupVersion.ToString(),
            Metadata = new() { Name = name, NamespaceProperty = namespaceName }
        };
    }
}
