// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Restores integration packages and creates package probe manifests.
/// </summary>
internal interface INuGetService
{
    /// <summary>
    /// Restores packages to the cache and creates a package probe manifest.
    /// </summary>
    /// <param name="packages">The packages to restore.</param>
    /// <param name="targetFramework">The target framework.</param>
    /// <param name="runtimeIdentifier">The runtime identifier used to prefer runtime-specific assets in the generated layout.</param>
    /// <param name="sources">Additional NuGet sources.</param>
    /// <param name="workingDirectory">Working directory for nuget.config discovery and for resolving the workspace-local restore cache. Required.</param>
    /// <param name="nugetConfigPath">An explicit NuGet.config file to use during restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the package probe manifest.</returns>
    Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        CancellationToken ct = default);
}

/// <summary>
/// Restores integration packages in-process through the NuGet client libraries.
/// </summary>
internal sealed class BundleNuGetService : INuGetService
{
    private readonly ILogger<BundleNuGetService> _logger;
    private readonly INuGetClient _nuGetClient;

    public BundleNuGetService(
        ILogger<BundleNuGetService> logger,
        INuGetClient nuGetClient)
    {
        _logger = logger;
        _nuGetClient = nuGetClient;
    }

    public async Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var packageList = packages.ToList();
        if (packageList.Count == 0)
        {
            throw new ArgumentException("At least one package is required", nameof(packages));
        }

        var sourceList = sources?.ToArray() ?? [];
        var packageHash = ComputePackageHash(packageList, targetFramework, runtimeIdentifier, sources: sourceList);
        var restoreCacheDirectory = GetPackageRestoreCacheDirectory(workingDirectory);
        var restoreDirectory = Path.Combine(restoreCacheDirectory, packageHash);
        var objectDirectory = Path.Combine(restoreDirectory, "obj");
        var manifestPath = Path.Combine(restoreDirectory, IntegrationPackageProbeManifest.FileName);
        var lockPath = Path.Combine(restoreDirectory, "restore.lock");

        // The package cache is shared by every AppHost in the workspace. Serialize the
        // restore and manifest write so consumers never observe partially written files.
        using var fileLock = await FileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);

        if (File.Exists(manifestPath) && TryValidatePackageManifest(manifestPath, _logger))
        {
            _logger.LogDebug("Using cached package manifest at {Path}", manifestPath);
            return manifestPath;
        }

        Directory.CreateDirectory(objectDirectory);
        _logger.LogDebug("Restoring {Count} integration packages in-process", packageList.Count);

        var restoredPackages = await _nuGetClient.RestoreAsync(
            packageList,
            targetFramework,
            runtimeIdentifier,
            objectDirectory,
            sourceList,
            nugetConfigPath,
            workingDirectory,
            ct).ConfigureAwait(false);

        await _nuGetClient.WriteManifestAsync(
            restoredPackages,
            manifestPath,
            targetFramework,
            runtimeIdentifier,
            ct).ConfigureAwait(false);

        _logger.LogDebug("Package manifest created at {Path}", manifestPath);
        return manifestPath;
    }

    private static bool TryValidatePackageManifest(string manifestPath, ILogger logger)
    {
        try
        {
            _ = IntegrationPackageProbeManifest.Load(manifestPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Cached package manifest {ManifestPath} is invalid and will be regenerated.", manifestPath);
            return false;
        }
    }

    internal static string ComputePackageHash(
        List<(string Id, string Version)> packages,
        string tfm,
        string? runtimeIdentifier,
        string? managedPath = null,
        IEnumerable<string>? sources = null)
    {
        var content = string.Join(
            ";",
            packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Select(package => $"{package.Id}:{package.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";client:{GetClientFingerprint(managedPath)}";

        if (sources?.ToArray() is { Length: > 0 } sourceList)
        {
            content += $";sources:{string.Join("|", sourceList.OrderBy(source => source, StringComparer.OrdinalIgnoreCase))}";
        }

        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(content));
        return hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetClientFingerprint(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            try
            {
                var fileInfo = new FileInfo(explicitPath);
                return fileInfo.Exists
                    ? $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}"
                    : "<missing>";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return "<error>";
            }
        }

        return typeof(BundleNuGetService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "<unknown>";
    }

    private static string GetPackageRestoreCacheDirectory(string workingDirectory)
    {
        var integrationCacheDirectory = ConfigurationHelper.GetIntegrationCacheDirectory(
            new DirectoryInfo(Path.GetFullPath(workingDirectory)));
        return Path.Combine(integrationCacheDirectory.FullName, "package-restore");
    }
}
