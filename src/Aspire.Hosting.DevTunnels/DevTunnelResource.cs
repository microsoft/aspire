// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DevTunnels;

/// <summary>
/// A resource representing a persistent dev tunnel that runs for the life of the AppHost.
/// </summary>
/// <param name="name"></param>
/// <param name="tunnelId"></param>
/// <param name="command"></param>
/// <param name="workingDirectory"></param>
/// <param name="options"></param>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, TunnelId = {TunnelId}")]
public sealed class DevTunnelResource(string name, string tunnelId, string command, string workingDirectory, DevTunnelOptions? options = null)
    : ExecutableResource(name, command, workingDirectory)
{
    /// <summary>
    /// Options controlling how this tunnel is created and managed.
    /// </summary>
    public DevTunnelOptions Options { get; } = options ?? new DevTunnelOptions();

    /// <summary>
    /// The unique ID for the dev tunnel.
    /// </summary>
    public string TunnelId { get; init; } = tunnelId;

    /// <summary>
    /// Gets the fully qualified tunnel ID including the region suffix if a region is specified.
    /// </summary>
    internal string ResolvedTunnelId => Options.Region is not null ? $"{TunnelId}.{Options.RegionCode}" : TunnelId;

    internal List<DevTunnelPortResource> Ports { get; } = [];

    internal DevTunnelStatus? LastKnownStatus { get; set; }

    internal DevTunnelAccessStatus? LastKnownAccessStatus { get; set; }
}

/// <summary>
/// A resource representing a single forwarded endpoint/port on a dev tunnel.
/// Contains an endpoint that resolves to the public tunnel URL of this port.
/// </summary>
public sealed class DevTunnelPortResource : Resource, IResourceWithServiceDiscovery, IResourceWithWaitSupport
{
    /// <summary>
    /// The name of the endpoint within this resource that represents the public URL of the tunnel for this port.
    /// </summary>
    internal const string TunnelEndpointName = "tunnel";

    /// <summary>
    /// Initializes a new instance of the <see cref="DevTunnelPortResource"/> class, representing a single forwarded endpoint/port on a dev tunnel.
    /// </summary>
    /// <param name="name">The name of the port resource.</param>
    /// <param name="tunnel">The parent <see cref="DevTunnelResource"/> this port belongs to.</param>
    /// <param name="targetEndpoint">The endpoint to be forwarded through the tunnel.</param>
    /// <param name="options">Options controlling how this port is exposed.</param>
    public DevTunnelPortResource(
        string name,
        DevTunnelResource tunnel,
        EndpointReference targetEndpoint,
        DevTunnelPortOptions? options = null) : base(name)
    {
        DevTunnel = tunnel;
        Options = options ?? new DevTunnelPortOptions();
        TargetEndpoint = targetEndpoint;
        TunnelEndpointAnnotation = new EndpointAnnotation(
            System.Net.Sockets.ProtocolType.Tcp,
            "https",
            transport: null,
            name: TunnelEndpointName,
            isProxied: false);
        TunnelEndpoint = new(targetEndpoint.Resource, TunnelEndpointAnnotation);
    }

    /// <summary>
    /// The dev tunnel this port belongs to.
    /// </summary>
    public DevTunnelResource DevTunnel { get; }

    /// <summary>
    /// Options controlling how this port is exposed.
    /// </summary>
    public DevTunnelPortOptions Options { get; }
    internal EndpointReference TunnelEndpoint { get; }
    internal EndpointAnnotation TunnelEndpointAnnotation { get; }
    internal EndpointReference TargetEndpoint { get; init; }
    internal DevTunnelPort? LastKnownStatus { get; set; }
    internal DevTunnelAccessStatus? LastKnownAccessStatus { get; set; }
}
