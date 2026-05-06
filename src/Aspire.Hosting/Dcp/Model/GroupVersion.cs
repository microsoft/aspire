// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Model;

internal struct GroupVersion
{
    public string Group { get; set; }
    public string Version { get; set; }

    public override string ToString() => $"{Group}/{Version}";
}

// Opt this class into the repo-internal ASPIREINT001 analyzer's catalog and
// scope its constants to DCP code. Inside Aspire.Hosting.Dcp.* the analyzer
// will prefer these constants over collisions with KnownResourceTypes.* etc.
[InternalKnownConstants(Namespaces = new[] { "Aspire.Hosting.Dcp" })]
internal static class Dcp
{
    public static GroupVersion GroupVersion { get; } = new GroupVersion
    {
        Group = "usvc-dev.developer.microsoft.com",
        Version = "v1"
    };

    public static readonly Schema Schema = new();

    public const string ExecutableKind = "Executable";
    public const string ContainerKind = "Container";
    public const string ContainerExecKind = "ContainerExec";
    public const string ContainerNetworkKind = "ContainerNetwork";
    public const string ServiceKind = "Service";
    public const string EndpointKind = "Endpoint";
    public const string ExecutableReplicaSetKind = "ExecutableReplicaSet";
    public const string ContainerVolumeKind = "ContainerVolume";
    public const string ContainerNetworkTunnelProxyKind = "ContainerNetworkTunnelProxy";

    static Dcp()
    {
        Schema.Add<Executable>(ExecutableKind, "executables");
        Schema.Add<Container>(ContainerKind, "containers");
        Schema.Add<ContainerNetwork>(ContainerNetworkKind, "containernetworks");
        Schema.Add<Service>(ServiceKind, "services");
        Schema.Add<Endpoint>(EndpointKind, "endpoints");
        Schema.Add<ExecutableReplicaSet>(ExecutableReplicaSetKind, "executablereplicasets");
        Schema.Add<ContainerVolume>(ContainerVolumeKind, "containervolumes");
        Schema.Add<ContainerExec>(ContainerExecKind, "containerexecs");
        Schema.Add<ContainerNetworkTunnelProxy>(ContainerNetworkTunnelProxyKind, "containernetworktunnelproxies");
    }
}
