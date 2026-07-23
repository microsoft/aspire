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
    private int _isOtlpEndpointResolved;
    private readonly object _otlpEndpointLock = new();

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
    /// Gets a value indicating whether the first OTLP endpoint allocation has been published.
    /// </summary>
    public bool IsOtlpEndpointResolved => Volatile.Read(ref _isOtlpEndpointResolved) != 0;

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

    internal OtlpEndpointUpdateResult UpdateOtlpEndpoint(string scheme, int port, string transport)
    {
        lock (_otlpEndpointLock)
        {
            var endpoint = OtlpStub.OtlpEndpoint;
            var hasChanged = !string.Equals(endpoint.UriScheme, scheme, StringComparisons.EndpointAnnotationName) ||
                endpoint.Port != port ||
                endpoint.TargetPort != port ||
                !string.Equals(endpoint.Transport, transport, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(endpoint.AllocatedEndpoint?.UriString, $"{scheme}://localhost:{port}", StringComparison.Ordinal);

            endpoint.UriScheme = scheme;
            endpoint.Port = port;
            endpoint.TargetPort = port;
            endpoint.Transport = transport;
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);

            return Interlocked.CompareExchange(ref _isOtlpEndpointResolved, 1, 0) == 0
                ? OtlpEndpointUpdateResult.FirstResolution
                : hasChanged
                    ? OtlpEndpointUpdateResult.Updated
                    : OtlpEndpointUpdateResult.Unchanged;
        }
    }
}

internal enum OtlpEndpointUpdateResult
{
    Unchanged,
    FirstResolution,
    Updated
}
