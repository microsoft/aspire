// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A custom entry point added with <c>WithEntryPoint</c>.
/// </summary>
/// <param name="Name">The name of the entry point and of its endpoint.</param>
/// <param name="TargetPort">The port Traefik listens on inside the container.</param>
/// <param name="IsUdp">Whether the entry point listens for UDP datagrams instead of TCP connections.</param>
/// <param name="IsTls">Whether Traefik terminates TLS on the entry point for every HTTP router on it (the <c>https</c> scheme).</param>
internal sealed record TraefikEntryPoint(string Name, int TargetPort, bool IsUdp, bool IsTls);

/// <summary>
/// An entry point of an external Traefik, described with <c>WithExternalEntryPoint</c>.
/// </summary>
/// <param name="Name">The name of the entry point on the external Traefik.</param>
/// <param name="IsTls">Whether clients reach the entry point over TLS (HTTPS for HTTP routes).</param>
/// <param name="Port">The public port clients connect to, or <see langword="null"/> for the standard port of the scheme.</param>
internal sealed record TraefikExternalEntryPoint(string Name, bool IsTls, int? Port);
