// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Shared;

namespace Aspire.Hosting;

internal static class DashboardRunStorage
{
    internal const int MaxApplicationDirectoryNameLength = 80;

    public static string GetApplicationDirectory(string? dataRoot, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(AspireHomeDirectory.GetDefault(), "dashboard");
        }

        return Path.Combine(Path.GetFullPath(dataRoot), GetApplicationDirectoryName(applicationName));
    }

    public static string GetApplicationDirectoryName(string applicationName)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationName);

        const int hashLength = 16;
        const int separatorLength = 1;
        var maxPrefixLength = MaxApplicationDirectoryNameLength - separatorLength - hashLength;
        var prefixBuilder = new StringBuilder(Math.Min(applicationName.Length, maxPrefixLength));

        foreach (var character in applicationName)
        {
            if (prefixBuilder.Length == maxPrefixLength)
            {
                break;
            }

            prefixBuilder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '-');
        }

        var prefix = prefixBuilder.ToString().Trim('-', '_');
        if (prefix.Length == 0)
        {
            prefix = "dashboard";
        }

        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(applicationName))).ToLowerInvariant();
        return $"{prefix}-{hash}";
    }
}