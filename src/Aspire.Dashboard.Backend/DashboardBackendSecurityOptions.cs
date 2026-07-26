// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Dashboard.Backend;

/// <summary>
/// Resolves whether the standalone backend is permitted to serve requests without delegating
/// authentication to an existing dashboard.
/// </summary>
/// <remarks>
/// The backend has no authentication authority of its own: it either proxies authorization to the
/// legacy dashboard (<see cref="IDashboardLegacyApiProxy.IsConfigured"/>) or it has none at all.
/// Because the API exposes raw resource values - environment variables, connection strings and
/// other properties the dashboard treats as sensitive - "no authority" must mean "no service"
/// rather than "no checks". Anonymous access is therefore opt-in through the exact same switches
/// the legacy dashboard documents, so an operator cannot end up unauthenticated by accident.
/// </remarks>
internal sealed class DashboardBackendSecurityOptions
{
    public DashboardBackendSecurityOptions(IConfiguration configuration)
    {
        AllowAnonymous = IsUnsecuredAllowed(configuration);
    }

    /// <summary>
    /// Gets a value indicating whether the operator explicitly opted into serving the dashboard
    /// API without authentication.
    /// </summary>
    public bool AllowAnonymous { get; }

    private static bool IsUnsecuredAllowed(IConfiguration configuration)
    {
        // Mirrors PostConfigureDashboardOptions: the anonymous switch wins outright, otherwise the
        // frontend auth mode must name Unsecured. Any other value (BrowserToken, OpenIdConnect)
        // describes an authority this host does not implement, so it is treated as "not allowed".
        if (GetBool(configuration, DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey)
            ?? GetBool(configuration, DashboardConfigNames.Legacy.DashboardUnsecuredAllowAnonymousName.ConfigKey)
            ?? false)
        {
            return true;
        }

        var authMode = configuration[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey];

        return string.Equals(authMode, "Unsecured", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? GetBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        return string.IsNullOrEmpty(value)
            ? null
            : bool.TryParse(value, out var parsed) ? parsed : null;
    }
}
