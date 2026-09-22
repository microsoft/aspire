// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Dashboard.Configuration;

namespace Aspire.Dashboard.Authentication;

internal static class DashboardAuthenticationCookieNames
{
    private const string AuthCookieNamePrefix = ".Aspire.Dashboard.Auth";
    private const string HttpAuthCookieNamePrefix = ".Aspire.Dashboard.Auth.Http";

    public static (string AuthCookieName, string HttpAuthCookieName) Create(string applicationName)
    {
        const int maxApplicationNameLength = 32;

        var nameBuilder = new StringBuilder();

        foreach (var character in applicationName)
        {
            if (nameBuilder.Length == maxApplicationNameLength)
            {
                break;
            }

            nameBuilder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '-');
        }

        var sanitizedApplicationName = nameBuilder.ToString().Trim('-', '_');
        if (sanitizedApplicationName.Length == 0)
        {
            sanitizedApplicationName = DashboardOptions.DefaultApplicationName;
        }
        sanitizedApplicationName = sanitizedApplicationName.ToLowerInvariant();

        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(applicationName))).ToLowerInvariant();
        var suffix = $"{sanitizedApplicationName}-{hash}";

        return ($"{AuthCookieNamePrefix}.{suffix}", $"{HttpAuthCookieNamePrefix}.{suffix}");
    }
}