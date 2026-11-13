// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Authentication.Connection;

internal static class ConnectionTypeHttpContextExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> when the request arrived on a connection that serves the dashboard frontend.
    /// Browser-only behavior, such as page redirects, shouldn't run on OTLP-only endpoints.
    /// </summary>
    /// <remarks>
    /// Kestrel endpoints always have <see cref="IConnectionTypeFeature"/> set. A request without the feature
    /// didn't come through a dashboard endpoint (for example, an in-memory test server), so it's treated as frontend.
    /// </remarks>
    public static bool IsFrontendConnection(this HttpContext context)
    {
        var feature = context.Features.Get<IConnectionTypeFeature>();
        return feature is null || feature.ConnectionTypes.Contains(ConnectionType.Frontend);
    }
}
