// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dcp;

internal interface IDcpDependencyCheckService
{
    Task<DcpInfo?> GetDcpInfoAsync(bool force = false, CancellationToken cancellationToken = default);
}

internal sealed class DcpInfo
{
    [JsonPropertyName("version")]
    public string? VersionString { get; set; }

    [JsonPropertyName("containers")]
    public DcpContainersInfo? Containers { get; set; }

    [JsonIgnore]
    public Version? Version { get; set; }
}

internal sealed class DcpContainersInfo
{
    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("installed")]
    public bool Installed { get; set; } = false;

    [JsonPropertyName("running")]
    public bool Running { get; set; } = false;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("hostName")]
    public string? HostName { get; set; }

    internal string ContainerHostName =>
        HostName ?? Runtime switch
        {
            "podman" => "host.containers.internal",
            _ => KnownHostNames.DockerDesktopHostBridge,
        };
}
