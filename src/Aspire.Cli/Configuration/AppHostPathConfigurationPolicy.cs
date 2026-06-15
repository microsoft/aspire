// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Configuration;

internal static class AppHostPathConfigurationPolicy
{
    public const string LegacyAppHostPathKey = "appHostPath";
    public const string AppHostKey = "appHost";
    public const string PathKey = "path";
    public const string AppHostPathKey = AppHostKey + "." + PathKey;
    public const string AppHostPathConfigurationKey = AppHostKey + ":" + PathKey;

    public static bool IsLegacyAppHostPathKey(string key) =>
        string.Equals(key, LegacyAppHostPathKey, StringComparison.OrdinalIgnoreCase);

    public static bool IsHierarchicalAppHostPathKey(string key)
    {
        var normalizedKey = key.Replace(':', '.');

        return string.Equals(normalizedKey, AppHostPathKey, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGloballySettableKey(string key)
    {
        return !IsHierarchicalAppHostPathKey(key)
            && !IsLegacyAppHostPathKey(key);
    }

    public static bool TryFindAppHostPathKey(IConfiguration configuration, out string? key)
    {
        key = null;

        if (configuration[LegacyAppHostPathKey] is not null)
        {
            key = LegacyAppHostPathKey;
            return true;
        }

        if (configuration[AppHostPathConfigurationKey] is not null)
        {
            key = AppHostPathKey;
            return true;
        }

        return false;
    }
}
