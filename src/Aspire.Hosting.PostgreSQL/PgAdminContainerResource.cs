// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Postgres;

/// <summary>
/// Represents a container resource for PGAdmin.
/// </summary>
/// <param name="name">The name of the container resource.</param>
public sealed class PgAdminContainerResource(string name) : ContainerResource(name)
{
    internal const string PrimaryEndpointName = "http";

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for pgAdmin.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);
}
