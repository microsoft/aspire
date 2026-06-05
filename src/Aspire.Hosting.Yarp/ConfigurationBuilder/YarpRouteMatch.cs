// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents route match criteria for a YARP route.
/// </summary>
[AspireDto]
internal sealed class YarpRouteMatch
{
    /// <summary>
    /// Gets or sets the path pattern to match.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the HTTP methods to match.
    /// </summary>
    public string[]? Methods { get; set; }

    /// <summary>
    /// Gets or sets the host headers to match.
    /// </summary>
    public string[]? Hosts { get; set; }

    /// <summary>
    /// Gets or sets the header match criteria.
    /// </summary>
    public YarpRouteHeaderMatch[]? Headers { get; set; }

    /// <summary>
    /// Gets or sets the query parameter match criteria.
    /// </summary>
    public YarpRouteQueryParameterMatch[]? QueryParameters { get; set; }
}
