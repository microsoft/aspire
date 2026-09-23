// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Dashboard.Configuration;

namespace Aspire.Dashboard.Utils;

/// <summary>
/// Creates a stable, collision-resistant key segment from a dashboard application name.
/// </summary>
internal static class DashboardApplicationNameKey
{
    public static string Create(string applicationName)
    {
        ArgumentNullException.ThrowIfNull(applicationName);

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

        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(applicationName))).ToLowerInvariant();
        return $"{sanitizedApplicationName.ToLowerInvariant()}-{hash}";
    }
}
