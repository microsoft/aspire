// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using k8s.Models;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class ContainerVolumeResetSpec
{
    [JsonPropertyName("containerName")]
    public required string ContainerName { get; init; }

    [JsonPropertyName("containerUid")]
    public required string ContainerUid { get; init; }
}

internal sealed record ContainerVolumeResetStatus : V1Status
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("containerRemoved")]
    public bool ContainerRemoved { get; init; }

    [JsonPropertyName("volumes")]
    public List<string>? Volumes { get; init; }

    [JsonPropertyName("consumers")]
    public List<ContainerVolumeResetConsumer>? Consumers { get; init; }
}

internal sealed class ContainerVolumeResetConsumer
{
    [JsonPropertyName("volumeName")]
    public string? VolumeName { get; init; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; init; }

    [JsonPropertyName("containerId")]
    public string? ContainerId { get; init; }
}

internal static class ContainerVolumeResetState
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>
/// An operation that resets the owned named volumes of a specific Container instance.
/// </summary>
internal sealed class ContainerVolumeReset : CustomResource<ContainerVolumeResetSpec, ContainerVolumeResetStatus>, IKubernetesStaticMetadata
{
    [JsonConstructor]
    public ContainerVolumeReset(ContainerVolumeResetSpec spec) : base(spec) { }

    public static ContainerVolumeReset Create(string name, string containerName, string containerUid)
    {
        var operation = new ContainerVolumeReset(new ContainerVolumeResetSpec
        {
            ContainerName = containerName,
            ContainerUid = containerUid
        })
        {
            Kind = Dcp.ContainerVolumeResetKind,
            ApiVersion = Dcp.GroupVersion.ToString()
        };
        operation.Metadata.Name = name;
        operation.Metadata.NamespaceProperty = string.Empty;

        return operation;
    }

    [JsonIgnore]
    public bool IsActive => Status?.State is not (ContainerVolumeResetState.Succeeded or ContainerVolumeResetState.Failed);

    public static string ObjectKind => Dcp.ContainerVolumeResetKind;
}
