// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Yarp.ReverseProxy.Configuration;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents a header-based route match for a YARP route.
/// </summary>
[AspireDto]
internal sealed class YarpRouteHeaderMatch
{
    /// <summary>
    /// Gets or sets the header name to match.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the header values to match.
    /// </summary>
    public string[]? Values { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the header comparison is case-sensitive.
    /// </summary>
    public bool IsCaseSensitive { get; set; }

    /// <summary>
    /// Gets or sets the matching mode for the header comparison.
    /// </summary>
    public HeaderMatchMode Mode { get; set; }
}
