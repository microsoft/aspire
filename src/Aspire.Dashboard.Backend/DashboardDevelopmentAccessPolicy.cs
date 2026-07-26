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
            var origins = context.RequestServices.GetRequiredService<DashboardBackendOriginPolicy>();

            if (!IsLoopback(context.Connection.LocalIpAddress)
                || !IsAllowedHost(context.Request.Host.Host)
                || !origins.IsAllowedOrigin(context))
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

    internal static bool IsAllowedHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IsLoopback(address);
    }

    /// <summary>
    /// Validates an <em>outbound</em> target this host is configured to call, such as the legacy
    /// dashboard URL. Restricting it to loopback keeps a misconfigured or attacker-supplied
    /// configuration value from turning the backend into a server-side request forgery vector that
    /// replays the caller's dashboard credentials to an arbitrary host.
    /// </summary>
    /// <param name="origin">An absolute origin, for example <c>http://localhost:16310</c>.</param>
    internal static bool IsLoopbackTarget(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && IsAllowedHost(uri.Host);
    }
}

/// <summary>
/// Decides which browser origins may reach the dashboard backend.
/// </summary>
/// <remarks>
/// <para>
/// Browsers do not apply the same-origin policy to WebSocket upgrades, and ASP.NET Core's
/// antiforgery middleware does not gate them either, so an <c>Origin</c> check is the only thing
/// standing between this host and Cross-Site WebSocket Hijacking. Accepting <em>any</em> loopback
/// origin is not sufficient: every locally running development server, test harness, or
/// npm-installed tool shares the loopback address space, so a hostile page served from any of them
/// could ride the delegated dashboard session and read resource secrets or drive a terminal.
/// </para>
/// <para>
/// The rule therefore matches the terminal proxy's check: the origin must equal the request's own
/// scheme, host, and port. Split-origin development - where the Vite dev server proxies API calls
/// and rewrites only the <c>Host</c> header, leaving <c>Origin</c> pointing at the dev server -
/// requires the operator to name that origin explicitly through
/// <c>DashboardBackend:AllowedOrigins</c>.
/// </para>
/// <para>
/// See <see href="https://datatracker.ietf.org/doc/html/rfc6455#section-10.2"/>.
/// </para>
/// </remarks>
internal sealed class DashboardBackendOriginPolicy
{
    internal const string AllowedOriginsKey = "DashboardBackend:AllowedOrigins";

    private readonly HashSet<string> _additionalOrigins;

    public DashboardBackendOriginPolicy(IConfiguration configuration)
    {
        _additionalOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Accepts a separated list of absolute origins, for example:
        //   DashboardBackend:AllowedOrigins = "http://localhost:1431;http://127.0.0.1:1431"
        var configured = configuration[AllowedOriginsKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        foreach (var candidate in configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                _additionalOrigins.Add(FormatOrigin(uri.Scheme, uri.Host, uri.IsDefaultPort ? null : uri.Port));
            }
        }
    }

    public bool IsAllowedOrigin(HttpContext context)
    {
        var values = context.Request.Headers.Origin;

        if (StringValues.IsNullOrEmpty(values))
        {
            // Browsers always send Origin on WebSocket upgrades, so a missing Origin there is
            // itself suspicious. Ordinary same-origin navigations and non-browser clients such as
            // curl legitimately omit it.
            return !context.WebSockets.IsWebSocketRequest;
        }

        if (values.Count is not 1
            || !Uri.TryCreate(values[0], UriKind.Absolute, out var origin)
            || (origin.Scheme is not "http" && origin.Scheme is not "https"))
        {
            return false;
        }

        var originValue = FormatOrigin(origin.Scheme, origin.Host, origin.IsDefaultPort ? null : origin.Port);

        if (_additionalOrigins.Contains(originValue))
        {
            return true;
        }

        var host = context.Request.Host;
        if (!host.HasValue)
        {
            return false;
        }

        // HostString.Port is null when the request omitted an explicit port, which is the same
        // normalization Uri.IsDefaultPort applies to the origin above.
        var expected = FormatOrigin(context.Request.Scheme, host.Host, host.Port);

        return string.Equals(originValue, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOrigin(string scheme, string host, int? port)
    {
        return port is { } value
            ? $"{scheme}://{host}:{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{scheme}://{host}";
    }
}
