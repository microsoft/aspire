// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;

namespace Aspire.Dashboard.Configuration;

/// <summary>
/// This endpoint info represents a resolved endpoint with its binding address, actual IP endpoint, and whether it uses HTTPS.
/// Useful when the port is dynamically assigned (port 0) but you need to address the endpoint with its resolved address.
/// </summary>
public record ResolvedEndpointInfo(BindingAddress BindingAddress, IPEndPoint EndPoint, bool IsHttps)
{
    public string GetResolvedAddress(bool replaceIPAnyWithLocalhost = false)
    {
        if (!IsAnyIPHost(BindingAddress.Host))
        {
            return BindingAddress.Scheme.ToLowerInvariant() + Uri.SchemeDelimiter + BindingAddress.Host.ToLowerInvariant() + ":" + EndPoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        if (replaceIPAnyWithLocalhost)
        {
            // Clicking on an any IP host link, e.g. http://0.0.0.0:1234, doesn't work.
            // Instead, write localhost so the link at least has a chance to work when the container and browser are on the same machine.
            return BindingAddress.Scheme.ToLowerInvariant() + Uri.SchemeDelimiter + "localhost:" + EndPoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        return BindingAddress.Scheme.ToLowerInvariant() + Uri.SchemeDelimiter + EndPoint.ToString();

        static bool IsAnyIPHost(string host)
        {
            // It's ok to use IPAddress.ToString here because the string is cached inside IPAddress.
            // BindingAddress retains the brackets in an IPv6 URL host, for example http://[::]:4317.
            return host is "*" or "+" or "[::]" || host == IPAddress.Any.ToString() || host == IPAddress.IPv6Any.ToString();
        }
    }
}
