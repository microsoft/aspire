// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Tests.Shared;

internal sealed record CliPackageInfo(string PackageId, string Version, string PackagePath);

internal static class CliPackageDiscovery
{
    internal const string AspireCliPackageId = "Aspire.Cli";

    private const string AspireCliPackagePrefix = AspireCliPackageId + ".";
    private const string NupkgSuffix = ".nupkg";
    private static readonly Regex s_versionPattern = new(@"^[0-9A-Za-z.\-]+$", RegexOptions.Compiled);
    private static readonly Regex s_cliPointerPackagePattern = new(@"^Aspire\.Cli\.\d", RegexOptions.Compiled);

    internal static bool IsValidVersion(string version)
    {
        return s_versionPattern.IsMatch(version);
    }

    internal static CliPackageInfo FindAspireCliPointerPackage(string packageDirectory)
    {
        return TryFindAspireCliPointerPackage(packageDirectory)
            ?? throw new InvalidOperationException(
                $"No Aspire.Cli tool nupkg found in '{packageDirectory}'. Expected exactly one non-symbol Aspire.Cli.{{version}}.nupkg package. Available files: {GetAvailableAspireCliPackageNames(packageDirectory)}");
    }

    internal static CliPackageInfo? TryFindAspireCliPointerPackage(string packageDirectory)
    {
        var matches = Directory.GetFiles(packageDirectory, "Aspire.Cli.*.nupkg")
            .Select(path => new CliPackageInfo(AspireCliPackageId, GetVersion(path), path))
            .Where(package => IsPointerPackageFileName(Path.GetFileName(package.PackagePath) ?? string.Empty))
            .OrderBy(package => Path.GetFileName(package.PackagePath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Found {matches.Count} Aspire.Cli pointer nupkg files in '{packageDirectory}': {string.Join(", ", matches.Select(package => Path.GetFileName(package.PackagePath)))}. " +
                "Expected exactly one non-symbol Aspire.Cli.{version}.nupkg package.");
        }

        var match = matches[0];
        if (!IsValidVersion(match.Version))
        {
            throw new InvalidOperationException(
                $"Invalid Aspire.Cli nupkg version '{match.Version}' in '{Path.GetFileName(match.PackagePath)}'. " +
                "Expected only alphanumeric characters, dots, and dashes.");
        }

        return match;
    }

    private static bool IsPointerPackageFileName(string fileName)
    {
        return fileName.EndsWith(NupkgSuffix, StringComparison.OrdinalIgnoreCase) &&
            !fileName.Contains(".symbols.", StringComparison.OrdinalIgnoreCase) &&
            s_cliPointerPackagePattern.IsMatch(fileName);
    }

    private static string GetVersion(string packagePath)
    {
        var fileName = Path.GetFileName(packagePath);
        if (fileName is null)
        {
            throw new InvalidOperationException($"Could not get file name from package path '{packagePath}'.");
        }

        return fileName[AspireCliPackagePrefix.Length..^NupkgSuffix.Length];
    }

    private static string GetAvailableAspireCliPackageNames(string packageDirectory)
    {
        var packageNames = Directory.GetFiles(packageDirectory, "Aspire.Cli.*")
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", packageNames);
    }
}
