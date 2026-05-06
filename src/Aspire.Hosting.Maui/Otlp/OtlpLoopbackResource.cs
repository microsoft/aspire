// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Otlp;

/// <summary>
/// Represents a synthetic OTLP resource that acts as a loopback endpoint for service discovery.
/// </summary>
/// <remarks>
/// This resource is used internally for MAUI OTLP configurations (especially with dev tunnels).
/// It creates an endpoint annotation that can be referenced by MAUI platform resources through service discovery.
/// The endpoint always points to localhost at the specified port and scheme, but can be tunneled externally.
/// </remarks>
internal sealed class OtlpLoopbackResource : Resource, IResourceWithEndpoints
{
    /// <summary>
    /// Initializes a new instance of <see cref="OtlpLoopbackResource"/>.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="port">The port number for the OTLP endpoint.</param>
    /// <param name="scheme">The URI scheme (http or https).</param>
    public OtlpLoopbackResource(string name, int port, string scheme) : base(name)
    {
        // Create an endpoint annotation for service discovery
        // This endpoint represents the OTLP collector endpoint that MAUI apps will connect to
        Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            uriScheme: scheme,
            name: "otlp",
            port: port,
            isProxied: false)
        {
            // TargetHost = localhost means this resource is running on the local machine
            // When tunneled through dev tunnels, the service discovery will rewrite this to the tunnel URL
            TargetHost = KnownHostNames.Localhost
        });
    }
}
