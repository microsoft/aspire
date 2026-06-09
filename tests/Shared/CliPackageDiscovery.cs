// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Tests.Shared;

internal sealed record CliPackageInfo(string PackageId, string Version, string PackagePath);

internal static partial class CliPackageDiscovery
{
    internal const string AspireCliPackageId = "Aspire.Cli";

    private const string AspireCliPackagePrefix = AspireCliPackageId + ".";
    private const string NupkgSuffix = ".nupkg";

    internal static bool IsValidVersion(string version)
    {
        return VersionPattern().IsMatch(version);
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
            .Where(path => IsPointerPackageFileName(Path.GetFileName(path)))
            .Select(path => new CliPackageInfo(AspireCliPackageId, GetVersion(path), path))
            .OrderBy(package => Path.GetFileName(package.PackagePath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Found {matches.Count} Aspire.Cli pointer nupkg files in '{packageDirectory}': {string.Join(", ", matches.Select(p => Path.GetFileName(p.PackagePath)))}. Expected exactly one Aspire.Cli.{{version}}.nupkg package.");
        }

        return matches[0];
    }

    private static bool IsPointerPackageFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || fileName.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase)
            || !fileName.StartsWith(AspireCliPackagePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(NupkgSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RidSpecificPackagePattern().IsMatch(fileName))
        {
            return false;
        }

        var version = fileName[AspireCliPackagePrefix.Length..^NupkgSuffix.Length];
        return IsValidVersion(version);
    }

    private static string GetVersion(string packagePath)
    {
        var fileName = Path.GetFileName(packagePath);
        var version = fileName[AspireCliPackagePrefix.Length..^NupkgSuffix.Length];
        if (!IsValidVersion(version))
        {
            throw new InvalidOperationException(
                $"Invalid Aspire.Cli nupkg version '{version}' in '{fileName}'. Expected only alphanumeric characters, dots, and dashes.");
        }

        return version;
    }

    private static string GetAvailableAspireCliPackageNames(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
        {
            return string.Empty;
        }

        return string.Join(", ", Directory.GetFiles(packageDirectory, "Aspire.Cli*.nupkg")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^[0-9A-Za-z.\-]+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^Aspire\.Cli\.(win|linux|linux-musl|osx)-(x64|arm64)\.", RegexOptions.IgnoreCase)]
    private static partial Regex RidSpecificPackagePattern();
}
