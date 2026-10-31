// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides collection operations over an endpoint's finalized browser-facing origins.
/// </summary>
[AspireExport(ExposeMethods = true, ExposeProperties = true)]
public sealed class EndpointBrowserOrigins
{
    private readonly EndpointReference _endpoint;

    internal EndpointBrowserOrigins(EndpointReference endpoint)
    {
        _endpoint = endpoint;
        First = endpoint.Property(EndpointProperty.BrowserOrigin);
    }

    /// <summary>
    /// Gets an endpoint-property expression for the preferred browser origin.
    /// </summary>
    public EndpointReferenceExpression First { get; }

    /// <summary>
    /// Creates an endpoint-property expression for comma-separated origins.
    /// </summary>
    /// <returns>The joined browser origins as an endpoint-property expression.</returns>
    public EndpointReferenceExpression Join() => _endpoint.Property(EndpointProperty.BrowserOrigins);

    /// <summary>
    /// Creates an endpoint-property expression for a JSON array of origins.
    /// </summary>
    /// <returns>The browser origins serialized as a JSON array.</returns>
    public EndpointReferenceExpression AsJson() => _endpoint.Property(EndpointProperty.BrowserOriginsJson);
}
