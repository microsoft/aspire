// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Maui.Otlp;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation that stores the OTLP dev tunnel configuration for a MAUI project.
/// This allows sharing a single dev tunnel infrastructure across multiple platform resources.
/// </summary>
internal sealed class OtlpDevTunnelConfigurationAnnotation : IResourceAnnotation
{
    private readonly object _otlpEndpointLock = new();
    private int _isOtlpEndpointResolved;

    /// <summary>
    /// The OTLP loopback stub resource that acts as the service discovery target.
    /// </summary>
    public OtlpLoopbackResource OtlpStub { get; }

    /// <summary>
    /// The resource builder for the OTLP stub (used for WithReference calls).
    /// </summary>
    public IResourceBuilder<OtlpLoopbackResource> OtlpStubBuilder { get; }

    /// <summary>
    /// The dev tunnel resource that tunnels the OTLP endpoint.
    /// </summary>
    public IResourceBuilder<DevTunnelResource> DevTunnel { get; }

    /// <summary>
    /// Gets a value indicating whether the dashboard OTLP listener has been resolved.
    /// </summary>
    public bool IsOtlpEndpointResolved => Volatile.Read(ref _isOtlpEndpointResolved) != 0;

    /// <summary>
    /// Gets or sets the maximum time to wait for DCP to publish the dashboard's concrete OTLP listener.
    /// </summary>
    internal TimeSpan RuntimeSnapshotResolutionTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public OtlpDevTunnelConfigurationAnnotation(
        OtlpLoopbackResource otlpStub,
        IResourceBuilder<OtlpLoopbackResource> otlpStubBuilder,
        IResourceBuilder<DevTunnelResource> devTunnel,
        bool isOtlpEndpointResolved)
    {
        OtlpStub = otlpStub;
        OtlpStubBuilder = otlpStubBuilder;
        DevTunnel = devTunnel;
        _isOtlpEndpointResolved = isOtlpEndpointResolved ? 1 : 0;
    }

    internal bool UpdateOtlpEndpoint(string scheme, int port, string transport)
    {
        lock (_otlpEndpointLock)
        {
            if (_isOtlpEndpointResolved != 0)
            {
                return false;
            }

            var endpoint = OtlpStub.OtlpEndpoint;
            endpoint.UriScheme = scheme;
            endpoint.Port = port;
            endpoint.TargetPort = port;
            endpoint.Transport = transport;
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);
            Volatile.Write(ref _isOtlpEndpointResolved, 1);

            return true;
        }
    }
}
