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
    /// The public dev tunnel endpoint used by MAUI platform environment variables.
    /// </summary>
    public EndpointReference TunnelEndpoint { get; }

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
        EndpointReference tunnelEndpoint,
        bool isOtlpEndpointResolved)
    {
        OtlpStub = otlpStub;
        OtlpStubBuilder = otlpStubBuilder;
        DevTunnel = devTunnel;
        TunnelEndpoint = tunnelEndpoint;
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

    internal bool TryFailOtlpEndpointResolution(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_otlpEndpointLock)
        {
            if (_isOtlpEndpointResolved != 0)
            {
                return false;
            }

            // DCP can resolve MAUI environment variables while the tunnel startup callback is
            // still waiting for the dashboard listener. Fault the synthetic target so the
            // failure-aware endpoint and protocol providers stop waiting. Do not fault the public
            // DevTunnel endpoint because its lifecycle reads the snapshot before replacing it on retry.
#pragma warning disable CS0618 // Type or member is obsolete
            OtlpStub.OtlpEndpoint.AllocatedEndpointSnapshot.SetException(exception);
#pragma warning restore CS0618 // Type or member is obsolete

            return true;
        }
    }
}
