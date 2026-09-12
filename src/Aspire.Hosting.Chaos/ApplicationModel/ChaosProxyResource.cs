// <copyright file="ChaosProxyResource.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a chaos proxy container - a YARP-based fault-injection
/// proxy with custom middleware compiled in. Drops between Aspire resources to intercept
/// and shape traffic.
/// </summary>
/// <remarks>
/// <para>
/// The chaos proxy is the dev-time / Aspire offering in the Azure Chaos Studio portfolio.
/// It is dev-only and is excluded from the Aspire publish manifest via
/// <c>ExcludeFromManifest()</c>.
/// </para>
/// <para>
/// M2 first slice: WithLatency only. Subsequent slices add WithError, WithReplayDuplicate,
/// the failFirst payload semantics (per D13), the runtime policy API, AddChaosProxyMesh,
/// and the Aspire.Hosting.Chaos.Azure companion package. See the design doc at
/// <c>docs/projects/aspire-chaos-proxy/aspire-chaos-proxy.plan.md</c>.
/// </para>
/// </remarks>
public class ChaosProxyResource : ContainerResource, IResourceWithServiceDiscovery
{
    /// <summary>The endpoint name for the HTTP listener.</summary>
    public const string HttpEndpointName = "http";

    /// <summary>
    /// The endpoint name for the HTTPS listener. Used by consumers that require https to
    /// their target — notably the Cosmos SDK in Gateway mode, which won't dial an http
    /// endpoint. The proxy presents a self-signed cert; consumers routed through this
    /// endpoint must accept any server cert (dev-only chaos infra).
    /// </summary>
    public const string HttpsEndpointName = "https";

    /// <summary>The container port the proxy listens on.</summary>
    internal const int ContainerPort = 8080;

    /// <summary>The container port the proxy's HTTPS listener binds.</summary>
    internal const int HttpsContainerPort = 8443;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosProxyResource"/> class.
    /// </summary>
    /// <param name="name">The resource name.</param>
    public ChaosProxyResource(string name)
        : base(name)
    {
    }
}
