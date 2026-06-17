// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Yarp.ReverseProxy.Configuration;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents a query-parameter-based route match for a YARP route.
/// </summary>
[AspireDto]
internal sealed class YarpRouteQueryParameterMatch
{
    /// <summary>
    /// Gets or sets the query parameter name to match.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the query parameter values to match.
    /// </summary>
    public string[]? Values { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the query parameter comparison is case-sensitive.
    /// </summary>
    public bool IsCaseSensitive { get; set; }

    /// <summary>
    /// Gets or sets the matching mode for the query parameter comparison.
    /// </summary>
    public QueryParameterMatchMode Mode { get; set; }
}
