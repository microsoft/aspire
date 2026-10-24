// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Postgres;

/// <summary>
/// Represents a container resource for pgweb.
/// </summary>
/// <param name="name">The name of the container resource.</param>
public sealed class PgWebContainerResource(string name) : ContainerResource(name)
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "pgweb";

    internal const string PrimaryEndpointName = "http";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the pgweb.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);
}
