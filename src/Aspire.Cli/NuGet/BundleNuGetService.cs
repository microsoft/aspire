// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using NuGet.ProjectModel;

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

        var sourceList = sources?.ToArray();

        // The restore is now performed by this process, so it is the tool whose changes must invalidate cached
        // manifests, just as the aspire-managed binary's size and timestamp did before.
        var packageHash = ComputePackageHash(
            packageList,
            targetFramework,
            runtimeIdentifier,
            Environment.ProcessPath,
            sourceList);
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

        // Failures keep the helper-era messages, which embed what the helper wrote to stderr, because
        // PrebuiltAppHostServer shows exception messages to users.
        try
        {
            await _nuGetClient.RestoreAsync(
                packageList,
                targetFramework,
                runtimeIdentifier,
                objectDirectory,
                sourceList ?? [],
                nugetConfigPath,
                workingDirectory,
                ct).ConfigureAwait(false);
        }
        catch (NuGetOperationException ex)
        {
            _logger.LogError("Package restore failed");
            _logger.LogError("Package restore stderr: {Error}", ex.Output);
            throw new InvalidOperationException($"Package restore failed: {ex.Output}", ex);
        }

        // The manifest is built from the assets file the restore just wrote, so asset selection
        // comes from NuGet rather than from a second walk over the package folders.
        try
        {
            await _nuGetClient.WriteManifestAsync(
                Path.Combine(objectDirectory, LockFileFormat.AssetsFileName),
                manifestPath,
                targetFramework,
                runtimeIdentifier,
                ct).ConfigureAwait(false);
        }
        catch (NuGetOperationException ex)
        {
            _logger.LogError("Manifest creation failed");
            _logger.LogError("Manifest creation stderr: {Error}", ex.Output);
            throw new InvalidOperationException($"Manifest creation failed: {ex.Output}", ex);
        }

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
        string? toolPath = null,
        IEnumerable<string>? sources = null)
    {
        // Same inputs and ordering as the helper-era key, so the same restores share a cache entry. In particular,
        // sources are sorted: their order does not change what NuGet restores.
        var content = string.Join(";", packages.OrderBy(package => package.Id).Select(package => $"{package.Id}:{package.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";tool:{GetToolFingerprint(toolPath)}";
        if (sources is not null)
        {
            content += $";sources:{string.Join("|", sources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase))}";
        }

        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(content));
        return hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetToolFingerprint(string? toolPath)
    {
        if (string.IsNullOrEmpty(toolPath))
        {
            return "<none>";
        }

        try
        {
            var fileInfo = new FileInfo(toolPath);
            return fileInfo.Exists
                ? $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}"
                : "<missing>";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "<error>";
        }
    }

    private static string GetPackageRestoreCacheDirectory(string workingDirectory)
    {
        var integrationCacheDirectory = ConfigurationHelper.GetIntegrationCacheDirectory(
            new DirectoryInfo(Path.GetFullPath(workingDirectory)));
        return Path.Combine(integrationCacheDirectory.FullName, "package-restore");
    }
}
