// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal static class IntegrationDisplayHelpers
{
    public static IntegrationSearchResultGroup GetIntegrationGroup(string packageId)
    {
        if (packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparisons.NuGetPackageId))
        {
            return IntegrationSearchResultGroup.CommunityToolkit;
        }

        if (HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId))
        {
            return IntegrationSearchResultGroup.Microsoft;
        }

        return IntegrationSearchResultGroup.ThirdParty;
    }

    public static string GetIntegrationGroupTitle(IntegrationSearchResultGroup group)
    {
        return group switch
        {
            IntegrationSearchResultGroup.Microsoft => AddCommandStrings.IntegrationGroupMicrosoft,
            IntegrationSearchResultGroup.CommunityToolkit => AddCommandStrings.IntegrationGroupCommunityToolkit,
            IntegrationSearchResultGroup.ThirdParty => AddCommandStrings.IntegrationGroupThirdParty,
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
    }
}

internal enum IntegrationSearchResultGroup
{
    Microsoft,
    CommunityToolkit,
    ThirdParty
}

internal sealed class IntegrationSearchDisplayResult
{
    public required string Name { get; init; }

    public required string Package { get; init; }

    public required string Version { get; init; }

    public required IntegrationSearchResultGroup Group { get; init; }
}
