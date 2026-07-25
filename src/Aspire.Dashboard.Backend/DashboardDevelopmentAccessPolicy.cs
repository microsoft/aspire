// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Microsoft.Extensions.Primitives;

namespace Aspire.Dashboard.Backend;

internal static class DashboardDevelopmentAccessPolicy
{
    public static IApplicationBuilder UseDashboardDevelopmentAccessPolicy(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // This migration host exposes the same raw resource values that the authenticated
            // dashboard uses for explicit secret reveal. Keep loopback connection and browser
            // origin restrictions as defense in depth around the delegated BrowserToken/OIDC
            // session, so an accidental 0.0.0.0 binding or hostile website cannot reach it.
            if (!IsLoopback(context.Connection.LocalIpAddress)
                || !IsAllowedHost(context.Request.Host.Host)
                || !IsAllowedOrigin(context.Request.Headers.Origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    internal static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            // TestServer does not populate a socket address. Production Kestrel connections do.
            return true;
        }

        return IPAddress.IsLoopback(address)
            || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
    }

    internal static bool IsAllowedOrigin(StringValues values)
    {
        if (StringValues.IsNullOrEmpty(values))
        {
            // Non-browser clients and same-origin GET requests commonly omit Origin.
            return true;
        }

        if (values.Count is not 1
            || !Uri.TryCreate(values[0], UriKind.Absolute, out var origin)
            || (origin.Scheme is not "http" && origin.Scheme is not "https"))
        {
            return false;
        }

        return IsAllowedHost(origin.Host);
    }

    internal static bool IsAllowedHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsLoopback(address);
    }
}
