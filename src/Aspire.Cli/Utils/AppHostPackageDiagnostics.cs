// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

/// <summary>
/// Helpers for logging AppHost package versions that affect guest-language code generation.
/// </summary>
internal static class AppHostPackageDiagnostics
{
    private static readonly string[] s_trackedPackageIds =
    [
        "Aspire.Hosting",
        "Aspire.Hosting.CodeGeneration.TypeScript",
        "Aspire.TypeSystem"
    ];

    public static string FormatTrackedPackageVersions(IEnumerable<(string Id, string? Version)> packages)
    {
        var packageVersions = packages
            .GroupBy(static p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First().Version, StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", s_trackedPackageIds.Select(id =>
            $"{id}={GetDisplayVersion(packageVersions.GetValueOrDefault(id))}"));
    }

    public static void LogRestoredPackageVersionsFromAssetsFile(ILogger logger, string assetsPath, string? manifestPath)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        try
        {
            if (!File.Exists(assetsPath))
            {
                logger.LogDebug(
                    "AppHost package diagnostics skipped because assets file was not found. AssetsPath: {AssetsPath}, ManifestPath: {ManifestPath}",
                    assetsPath,
                    manifestPath);
                return;
            }

            var versions = ReadPackageVersionsFromAssetsFile(assetsPath);
            logger.LogDebug(
                "Restored AppHost package versions. ManifestPath: {ManifestPath}, Packages: {PackageVersions}",
                manifestPath,
                FormatTrackedPackageVersions(versions));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(
                ex,
                "Failed to read AppHost package versions from assets file. AssetsPath: {AssetsPath}, ManifestPath: {ManifestPath}",
                assetsPath,
                manifestPath);
        }
    }

    private static IReadOnlyList<(string Id, string? Version)> ReadPackageVersionsFromAssetsFile(string assetsPath)
    {
        using var stream = File.OpenRead(assetsPath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return [];
        }

        var versions = new List<(string Id, string? Version)>();
        foreach (var library in libraries.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "package", StringComparison.OrdinalIgnoreCase) ||
                TryParseLibraryName(library.Name) is not { } package)
            {
                continue;
            }

            if (s_trackedPackageIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
            {
                versions.Add(package);
            }
        }

        return versions;
    }

    private static (string Id, string Version)? TryParseLibraryName(string libraryName)
    {
        var separatorIndex = libraryName.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex == libraryName.Length - 1)
        {
            return null;
        }

        return (libraryName[..separatorIndex], libraryName[(separatorIndex + 1)..]);
    }

    private static string GetDisplayVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "<not restored>" : version;
}
