// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Marks a resource that already joins an external Traefik's shared network because a middleware calls it.
/// </summary>
internal sealed class TraefikNetworkAnnotation(string network) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the shared network.
    /// </summary>
    public string Network { get; } = network;
}
