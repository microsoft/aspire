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

        // This prevents a page on another website from opening a WebSocket to the dashboard because the
        // browser sends that page's origin, which won't match the dashboard's host.
        //
        // Request.Host is client-controlled, so this same-origin check does not prevent DNS rebinding. In
        // authenticated modes, dashboard cookies are host-scoped and aren't sent to the attacker's host;
        // authorization remains the security boundary for sensitive data and operations. Deployments using
        // unsecured mode must rely on network isolation or host filtering to prevent rebinding access.
        //
        // The security documentation discusses hardening host access when anonymous access is enabled:
        // https://aspire.dev/dashboard/security-considerations/
        var expectedHost = context.Request.Host;
        if (!expectedHost.HasValue)
        {
            return false;
        }

        if (!Uri.TryCreate($"{context.Request.Scheme}://{expectedHost}", UriKind.Absolute, out var expectedUri))
        {
            return false;
        }

        return string.Equals(originUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) &&
            originUri.Port == expectedUri.Port;
    }
}