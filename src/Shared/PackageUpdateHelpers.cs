// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Semver;
#if CLI
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
#else
using NuGetPackage = Aspire.Shared.NuGetPackage;
#endif

namespace Aspire.Shared;

#if CLI
internal class NuGetPackageCli
#else
internal class NuGetPackage
#endif
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

internal static class PackageUpdateHelpers
{
    public static SemVersion? GetCurrentPackageVersion()
    {
        try
        {
            var versionString = GetCurrentAssemblyVersion();
            if (versionString == null)
            {
                return null;
            }

            // Remove any build metadata (e.g., +sha.12345) for comparison
            var cleanVersionString = versionString.Split('+')[0];
            return SemVersion.Parse(cleanVersionString, SemVersionStyles.Strict);
        }
        catch
        {
            return null;
        }
    }

    public static string? GetCurrentAssemblyVersion()
    {
        // Write some code that gets the informational assembly version of the current assembly and returns it as a string.
        var assembly = typeof(PackageUpdateHelpers).Assembly;
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        return informationalVersion;
    }

    public static SemVersion? GetNewerVersion(ILogger logger, SemVersion currentVersion, IEnumerable<NuGetPackage> availablePackages, SemVersion? storedVersion = null)
    {
        SemVersion? newestStable = null;
        SemVersion? newestPrerelease = null;

        foreach (var package in availablePackages)
        {
            if (SemVersion.TryParse(package.Version, SemVersionStyles.Strict, out var version))
            {
                ProcessNewVersion(version);
            }
        }

        if (storedVersion != null)
        {
            ProcessNewVersion(storedVersion);
        }

        logger.LogDebug(
            """
            Current version: {CurrentVersion}
            Newest stable version: {NewestStableVersion}
            Newest prerelease version: {NewestPrereleaseVersion}
            """, currentVersion, newestStable, newestPrerelease);

        // Apply notification rules
        if (currentVersion.IsPrerelease)
        {
            // Rule 1: If using a prerelease version where the version is lower than the latest stable version, prompt to upgrade
            if (newestStable is not null && SemVersion.PrecedenceComparer.Compare(currentVersion, newestStable) < 0)
            {
                logger.LogDebug("Current version {CurrentVersion} is prerelease and older than newest stable version {NewestStableVersion}.", currentVersion, newestStable);
                return newestStable;
            }

            // Rule 2: If using a prerelease version and there is a newer prerelease version, prompt to upgrade
            if (newestPrerelease is not null && SemVersion.PrecedenceComparer.Compare(currentVersion, newestPrerelease) < 0)
            {
                logger.LogDebug("Current version {CurrentVersion} is prerelease and older than newest prerelease version {NewestPrereleaseVersion}.", currentVersion, newestPrerelease);
                return newestPrerelease;
            }
        }
        else
        {
            // Rule 3: If using a stable version and there is a newer stable version, prompt to upgrade
            if (newestStable is not null && SemVersion.PrecedenceComparer.Compare(currentVersion, newestStable) < 0)
            {
                logger.LogDebug("Current version {CurrentVersion} is stable and older than newest stable version {NewestStableVersion}.", currentVersion, newestStable);
                return newestStable;
            }
        }

        logger.LogDebug("No newer version for the current version {CurrentVersion}.", currentVersion);
        return null;

        void ProcessNewVersion(SemVersion version)
        {
            if (version.IsPrerelease)
            {
                newestPrerelease = newestPrerelease is null || SemVersion.PrecedenceComparer.Compare(version, newestPrerelease) > 0 ? version : newestPrerelease;
            }
            else
            {
                newestStable = newestStable is null || SemVersion.PrecedenceComparer.Compare(version, newestStable) > 0 ? version : newestStable;
            }
        }
    }

    public static List<NuGetPackage> ParsePackageSearchResults(string stdout, string? packageId = null)
    {
        var foundPackages = new List<NuGetPackage>();

        using var document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("searchResult", out var searchResultsArray))
        {
            return [];
        }

        foreach (var sourceResult in searchResultsArray.EnumerateArray())
        {
            var source = sourceResult.GetProperty("sourceName").GetString()!;
            var sourcePackagesArray = sourceResult.GetProperty("packages");

            foreach (var packageResult in sourcePackagesArray.EnumerateArray())
            {
                var id = packageResult.GetProperty("id").GetString()!;

                var version = packageResult.TryGetProperty("latestVersion", out var latestVersionProp)
                    ? latestVersionProp.GetString()!
                    : packageResult.GetProperty("version").GetString()!;

                if (packageId == null || id == packageId)
                {
                    foundPackages.Add(new NuGetPackage
                    {
                        Id = id,
                        Version = version,
                        Source = source
                    });
                }
            }
        }

        return foundPackages;
    }
}
