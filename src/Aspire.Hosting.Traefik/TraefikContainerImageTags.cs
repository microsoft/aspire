// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

internal static class TraefikContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/traefik</remarks>
    public const string Image = "library/traefik";

    /// <remarks>v3.7</remarks>
    public const string Tag = "v3.7";

    /// <remarks>quay.io</remarks>
    public const string OAuth2ProxyRegistry = "quay.io";

    /// <remarks>oauth2-proxy/oauth2-proxy</remarks>
    public const string OAuth2ProxyImage = "oauth2-proxy/oauth2-proxy";

    /// <remarks>v7.15.5</remarks>
    public const string OAuth2ProxyTag = "v7.15.5";

    /// <remarks>docker.io</remarks>
    public const string SocketProxyRegistry = "docker.io";

    /// <remarks>tecnativa/docker-socket-proxy</remarks>
    public const string SocketProxyImage = "tecnativa/docker-socket-proxy";

    /// <remarks>v0.5.0</remarks>
    public const string SocketProxyTag = "v0.5.0";

    /// <summary>
    /// The digest of the multi-platform image index of <see cref="SocketProxyTag"/>.
    /// </summary>
    /// <remarks>
    /// The proxy holds the Docker socket, so the image is pinned by digest: a tag can be pushed again, a digest can't.
    /// It comes from a third party (<see href="https://github.com/Tecnativa/docker-socket-proxy"/>), not a Docker
    /// Official Image. When updating the tag, take the index digest from
    /// <c>docker buildx imagetools inspect docker.io/tecnativa/docker-socket-proxy:&lt;tag&gt;</c>.
    /// </remarks>
    public const string SocketProxySha256 = "1f5038b54f06c3e18422902cf00ba21803d1c97805aae032e5e6673d532d3459";
}
