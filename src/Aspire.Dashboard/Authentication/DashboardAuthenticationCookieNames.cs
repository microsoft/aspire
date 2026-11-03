// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Authentication;

internal static class DashboardAuthenticationCookieNames
{
    private const string AuthCookieNamePrefix = ".Aspire.Dashboard.Auth";
    private const string HttpAuthCookieNamePrefix = ".Aspire.Dashboard.Auth.Http";

    public static (string AuthCookieName, string HttpAuthCookieName) Create(string applicationName)
    {
        var suffix = DashboardApplicationNameKey.Create(applicationName);

        return ($"{AuthCookieNamePrefix}.{suffix}", $"{HttpAuthCookieNamePrefix}.{suffix}");
    }
}