// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

internal static class HostingIntegrationMetadata
{
    public const string CanonicalTag = "aspire";
    public const string HostingTag = "hosting";
    public const string DiscoveryQuery = $"tags:{CanonicalTag} tags:{HostingTag}";

    private static readonly string[] s_hostingDependencyPackageIds =
    [
        "Aspire.Hosting",
        "Aspire.Hosting.AppHost"
    ];

    public static IReadOnlyList<string> HostingDependencyPackageIds => s_hostingDependencyPackageIds;

    public static bool IsHostingDependencyPackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return s_hostingDependencyPackageIds.Contains(packageId, StringComparers.NuGetPackageId);
    }

    public static bool IsBuiltInHostingPackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var isBuiltInHostingPackage = packageId.StartsWith("Aspire.Hosting.", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparisons.NuGetPackageId);

        var isExcluded = packageId.StartsWith("Aspire.Hosting.AppHost", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("Aspire.Hosting.Sdk", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("Aspire.Hosting.Orchestration", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("Aspire.Hosting.Testing", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("Aspire.Hosting.Msi", StringComparisons.NuGetPackageId);

        return isBuiltInHostingPackage && !isExcluded;
    }

    public static bool IsKnownNonHostingAspirePackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        if (IsBuiltInHostingPackageId(packageId))
        {
            return false;
        }

        return packageId.StartsWith("Aspire.", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("CommunityToolkit.Aspire.", StringComparisons.NuGetPackageId) ||
            packageId.StartsWith("Microsoft.NET.Sdk.Aspire.", StringComparisons.NuGetPackageId);
    }
}
