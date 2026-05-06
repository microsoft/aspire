// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A network identifier used to specify the network context for resources within an Aspire application model.
/// </summary>
public record NetworkIdentifier(string Value);

/// <summary>
/// Provides known network identifiers for use within the Aspire application model API.
/// </summary> 
public static class KnownNetworkIdentifiers
{
    /// <summary>
    /// The network associated with the IP loopback interface (localhost).
    /// </summary>
    public static readonly NetworkIdentifier LocalhostNetwork = new NetworkIdentifier(KnownHostNames.Localhost);

    /// <summary>
    /// Represents public Internet (globally routable).
    /// </summary>
    public static readonly NetworkIdentifier PublicInternet = new NetworkIdentifier(".");

    /// <summary>
    /// Represents Aspire default, auto-created container network resource (not actual Docker/Podman network).
    /// </summary>
    public static readonly NetworkIdentifier DefaultAspireContainerNetwork = new NetworkIdentifier("aspire-container-network");
}

/// <summary>
/// Provides known host names for use within the Aspire application model API.
/// </summary>
public static class KnownHostNames
{
    /// <summary>
    /// The host name associated with the IP loopback interface (localhost).
    /// </summary>
    /// <remarks>
    /// In general, "localhost" resolves to multiple addresses. E.g. in dual-stack systems (IPv4 and IPv6, very common)
    /// "localhost" resolves at least to 127.0.0.1 and [::1]. On some systems there are multiple IPv4 networks associated
    /// with loopback interface and the number of potential addresses for "localhost" increases accordingly.
    /// </remarks>
    public const string Localhost = "localhost";

    /// <summary>
    /// The host name used to facilitate connections originating from containers and ending on the host network.
    /// </summary>
    public const string DefaultContainerTunnelHostName = "aspire.dev.internal";

    /// <summary>
    /// The host name used by Docker Desktop host network bridge.
    /// </summary>
    public const string DockerDesktopHostBridge = "host.docker.internal";
}
