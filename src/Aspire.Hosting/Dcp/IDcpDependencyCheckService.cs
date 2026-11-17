// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Shared;

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

    // When DCP cannot query the container runtime (for example, the runtime CLI times out), it reports
    // an empty runtime and host name. Treat an empty host name like a missing one so containers still
    // get a usable host address instead of a URL with no host.
    internal string ContainerHostName =>
        !string.IsNullOrEmpty(HostName) ? HostName : Runtime switch
        {
            KnownContainerRuntimes.Podman => "host.containers.internal",
            _ => "host.docker.internal",
        };
}
