// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Validates that a browser WebSocket request originated from the dashboard.
/// </summary>
internal static class WebSocketOriginValidator
{
    internal static bool IsSameOrigin(HttpContext context, out string originLogValue)
    {
        var origin = context.Request.Headers.Origin.ToString();
        originLogValue = string.IsNullOrEmpty(origin) ? "(none)" : origin;

        if (string.IsNullOrEmpty(origin) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
            !string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedHost = context.Request.Host;
        if (!expectedHost.HasValue)
        {
            return false;
        }

        // Uri.Authority omits default ports, so construct the authority explicitly
        // and compare it with HostString's equivalent normalized representation.
        var originAuthority = originUri.IsDefaultPort
            ? originUri.Host
            : originUri.Host + ":" + originUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return string.Equals(originAuthority, expectedHost.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}