// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Aspire.Dashboard.Authentication;

internal static class DashboardAuthenticationState
{
    /// <summary>
    /// Double-checks authentication state inside the dashboard as a defense-in-depth measure.
    /// Use in main layouts and infrastructure that launches UI.
    /// </summary>
    public static async Task EnsureAuthenticatedAsync(
        DashboardOptions dashboardOptions,
        Task<AuthenticationState>? authenticationState)
    {
        if (dashboardOptions.Frontend.AuthMode is FrontendAuthMode.Unsecured or null)
        {
            return;
        }

        if (authenticationState is null || (await authenticationState.ConfigureAwait(false)).User.Identity?.IsAuthenticated is not true)
        {
            throw new InvalidOperationException("Dashboard authentication is required, but the current user is not authenticated.");
        }
    }
}
