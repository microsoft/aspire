// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

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

    /// <summary>
    /// Whether this mapping routes Aspire.* packages to an existing local directory of
    /// <c>.nupkg</c> files rather than a remote feed. True when the filter targets Aspire
    /// packages specifically (not the <c>*</c> catch-all) and the source is a real directory
    /// instead of an http(s) feed URL. This is what lets a channel be recognized as locally
    /// backed even when it is named after an emulated identity (stable/daily/staging) — see
    /// <see cref="PackageChannel.IsBackedByLocalPackageDirectory"/>.
    /// </summary>
    public bool IsAspireDirectoryMapping =>
        PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase) &&
        PackageFilter != AllPackages &&
        !UrlHelper.IsHttpUrl(Source) &&
        Directory.Exists(Source);
}
