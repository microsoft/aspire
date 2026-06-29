// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Commands;

internal enum IntegrationDiscoveryScope
{
    Official,
    All,
    ThirdParty
}

internal enum ThirdPartyIntegrationDiscoveryMode
{
    Off,
    Ask,
    On
}

internal static class IntegrationDiscoveryScopeHelpers
{
    public const string OfficialValue = "official";
    public const string AllValue = "all";
    public const string ThirdPartyValue = "third-party";

    public static bool TryParse(string? value, out IntegrationDiscoveryScope scope)
    {
        scope = IntegrationDiscoveryScope.Official;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case OfficialValue:
                scope = IntegrationDiscoveryScope.Official;
                return true;
            case AllValue:
                scope = IntegrationDiscoveryScope.All;
                return true;
            case ThirdPartyValue:
                scope = IntegrationDiscoveryScope.ThirdParty;
                return true;
            default:
                return false;
        }
    }

    public static bool IncludesPackageId(this IntegrationDiscoveryScope scope, string packageId)
    {
        return scope.IncludesGroup(IntegrationDisplayHelpers.GetIntegrationGroup(packageId));
    }

    public static bool IncludesGroup(this IntegrationDiscoveryScope scope, IntegrationSearchResultGroup group)
    {
        return scope switch
        {
            IntegrationDiscoveryScope.Official => group is IntegrationSearchResultGroup.Microsoft or IntegrationSearchResultGroup.CommunityToolkit,
            IntegrationDiscoveryScope.All => true,
            IntegrationDiscoveryScope.ThirdParty => group is IntegrationSearchResultGroup.ThirdParty,
            _ => false
        };
    }

    public static IntegrationDiscoveryScope GetConfiguredScope(DirectoryInfo workingDirectory)
    {
        return GetConfiguredThirdPartyMode(workingDirectory) switch
        {
            ThirdPartyIntegrationDiscoveryMode.On => IntegrationDiscoveryScope.All,
            _ => IntegrationDiscoveryScope.Official
        };
    }

    public static ThirdPartyIntegrationDiscoveryMode GetConfiguredThirdPartyMode(DirectoryInfo workingDirectory)
    {
        var mode = AspireConfigFile.Load(workingDirectory.FullName)?.Integrations?.Discovery?.ThirdParty?.Mode;

        return TryParseThirdPartyMode(mode, out var parsedMode)
            ? parsedMode
            : ThirdPartyIntegrationDiscoveryMode.Off;
    }

    public static string[] GetConfiguredThirdPartyFeeds(DirectoryInfo workingDirectory)
    {
        return AspireConfigFile.Load(workingDirectory.FullName)?.Integrations?.Discovery?.ThirdParty?.Feeds?
            .Where(static feed => !string.IsNullOrWhiteSpace(feed))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public static HashSet<string>? GetConfiguredThirdPartyPackages(DirectoryInfo workingDirectory)
    {
        var packages = AspireConfigFile.Load(workingDirectory.FullName)?.Integrations?.Discovery?.ThirdParty?.Packages?
            .Where(static package => !string.IsNullOrWhiteSpace(package))
            .ToArray();

        return packages is { Length: > 0 }
            ? new HashSet<string>(packages, StringComparers.NuGetPackageId)
            : null;
    }

    public static bool IsPackageAllowed(this IntegrationDiscoveryScope scope, string packageId, HashSet<string>? thirdPartyPackageAllowlist)
    {
        if (!scope.IncludesPackageId(packageId))
        {
            return false;
        }

        return IntegrationDisplayHelpers.GetIntegrationGroup(packageId) is not IntegrationSearchResultGroup.ThirdParty ||
            thirdPartyPackageAllowlist is null ||
            thirdPartyPackageAllowlist.Contains(packageId);
    }

    public static bool TryParseThirdPartyMode(string? value, out ThirdPartyIntegrationDiscoveryMode mode)
    {
        mode = ThirdPartyIntegrationDiscoveryMode.Off;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = ThirdPartyIntegrationDiscoveryMode.Off;
                return true;
            case "ask":
                mode = ThirdPartyIntegrationDiscoveryMode.Ask;
                return true;
            case "on":
                mode = ThirdPartyIntegrationDiscoveryMode.On;
                return true;
            default:
                return false;
        }
    }
}
