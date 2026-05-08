// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

internal class PackageMapping(string PackageFilter, string source)
{
    public const string AllPackages = "*";
    public string PackageFilter { get; } = PackageFilter;
    public string Source { get; } = source;

    public bool MatchesPackageId(string packageId) => MatchesPackageId(PackageFilter, packageId);

    public static bool MatchesPackageId(string packageFilter, string packageId)
    {
        if (string.Equals(packageFilter, AllPackages, StringComparison.Ordinal))
        {
            return true;
        }

        if (packageFilter.EndsWith('*'))
        {
            return packageId.StartsWith(packageFilter[..^1], StringComparisons.NuGetPackageId);
        }

        return string.Equals(packageFilter, packageId, StringComparisons.NuGetPackageId);
    }
}
