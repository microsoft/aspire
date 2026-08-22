// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Determines how Azure Front Door distributes traffic across the origins of an origin group.
/// </summary>
/// <remarks>
/// See <see href="https://learn.microsoft.com/azure/frontdoor/routing-methods"/> for the routing behaviour
/// each of these produces.
/// </remarks>
public enum FrontDoorOriginRouting
{
    /// <summary>
    /// Every origin is equally preferred and Front Door routes each request to the closest healthy origin,
    /// measured by latency. This is the default and the usual choice for regional stamps of one application.
    /// </summary>
    LatencyBased,

    /// <summary>
    /// Origins are tried in declaration order: all traffic goes to the first origin while it is healthy, and
    /// falls back to the next one when it is not. Use this for an active/passive topology.
    /// </summary>
    Failover,

    /// <summary>
    /// Traffic is split across all healthy origins in proportion to their weights. Use this to send a
    /// deliberate share of traffic to each region, for example while validating a new region.
    /// </summary>
    Weighted
}

/// <summary>
/// The protocol Azure Front Door uses to health-probe the origins of an origin group.
/// </summary>
public enum FrontDoorHealthProbeProtocol
{
    /// <summary>
    /// Probe origins over HTTP.
    /// </summary>
    Http,

    /// <summary>
    /// Probe origins over HTTPS.
    /// </summary>
    Https
}
