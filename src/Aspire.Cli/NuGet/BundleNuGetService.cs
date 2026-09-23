// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using NuGet.ProjectModel;

namespace Aspire.Cli.NuGet;

internal sealed record NuGetSettingsInfo(
    IReadOnlyList<string> ConfigPaths,
    string CacheIdentity,
    IReadOnlyList<NuGetSourceInfo> Sources,
    IReadOnlyList<string> SensitiveSourceValues,
    bool PackageSourceMappingEnabled,
    IReadOnlyList<NuGetPackageSourceMapping> PackageSourceMappings,
    IReadOnlyList<string> DisabledPackageSourceKeys,
    IReadOnlyList<string> ReservedPackageSourceKeys,
    byte[] SourceIdentityKey);

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
    /// <param name="workingDirectory">Working directory for NuGet.config discovery and for resolving the workspace-local restore cache.</param>
    /// <param name="nugetConfigPaths">NuGet.config paths ordered from highest to lowest precedence.</param>
    /// <param name="nugetSettingsCacheIdentity">The cache identity computed from NuGet's effective ambient settings.</param>
    /// <param name="nugetConfigOverlayCacheIdentity">A stable cache identity for an invocation-scoped overlay.</param>
    /// <param name="additionalSensitiveSources">Additional source values that must be redacted from restore output.</param>
    /// <param name="globalPackagesFolderOverride">An optional global packages folder override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path to the package probe manifest.</returns>
    Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        IReadOnlyList<string>? nugetConfigPaths = null,
        string? nugetSettingsCacheIdentity = null,
        string? nugetConfigOverlayCacheIdentity = null,
        IEnumerable<string>? additionalSensitiveSources = null,
        string? globalPackagesFolderOverride = null,
        CancellationToken ct = default);
}

/// <summary>
/// Runs bundled NuGet operations in-process and owns their reusable restore cache.
/// </summary>
internal sealed class BundleNuGetService : INuGetService
{
    private readonly ILogger<BundleNuGetService> _logger;
    private readonly INuGetClient _nuGetClient;

    internal Func<byte[]> SourceIdentityKeyFactory { get; init; }
        = static () => RandomNumberGenerator.GetBytes(NuGetSourceIdentity.KeySizeInBytes);

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
        IReadOnlyList<string>? nugetConfigPaths = null,
        string? nugetSettingsCacheIdentity = null,
        string? nugetConfigOverlayCacheIdentity = null,
        IEnumerable<string>? additionalSensitiveSources = null,
        string? globalPackagesFolderOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var packageList = packages.ToList();
        if (packageList.Count == 0)
        {
            throw new ArgumentException("At least one package is required", nameof(packages));
        }

        var sourceList = sources?.ToArray();
        var sensitiveSources = (sourceList ?? [])
            .Concat(additionalSensitiveSources ?? [])
            .Where(PackageSourceOverrideMappings.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var nugetConfigCacheIdentity = ComputeNuGetConfigCacheIdentity(
            nugetSettingsCacheIdentity,
            nugetConfigOverlayCacheIdentity);
        var packageHash = ComputePackageHash(
            packageList,
            targetFramework,
            runtimeIdentifier,
            GetRestoreToolPath(),
            sourceList,
            nugetConfigCacheIdentity,
            globalPackagesFolderOverride);
        var restoreDirectory = Path.Combine(
            GetPackageRestoreCacheDirectory(workingDirectory),
            packageHash);
        var objectDirectory = Path.Combine(restoreDirectory, "obj");
        var manifestPath = Path.Combine(restoreDirectory, IntegrationPackageProbeManifest.FileName);
        var lockPath = Path.Combine(restoreDirectory, "restore.lock");

        // Reusable package caches are shared by every AppHost in the workspace and must remain
        // serialized while their manifest or project.assets.json file is being written.
        using var fileLock = await FileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);

        if (File.Exists(manifestPath) && TryValidatePackageManifest(manifestPath, _logger))
        {
            _logger.LogDebug("Using cached package manifest at {Path}", manifestPath);
            return manifestPath;
        }

        Directory.CreateDirectory(objectDirectory);
        _logger.LogDebug("Restoring {Count} integration packages in-process", packageList.Count);

        try
        {
            await _nuGetClient.RestoreAsync(
                packageList,
                targetFramework,
                runtimeIdentifier,
                objectDirectory,
                sourceList ?? [],
                nugetConfigPaths ?? [],
                workingDirectory,
                globalPackagesFolderOverride,
                sensitiveSources,
                ct).ConfigureAwait(false);
        }
        catch (NuGetOperationException ex)
        {
            var redactedOutput = PackageSourceRedactor.RedactOccurrences(ex.Output, sensitiveSources);
            _logger.LogError("Package restore failed");
            _logger.LogError("Package restore stderr: {Error}", redactedOutput);
            throw new InvalidOperationException($"Package restore failed: {redactedOutput}", ex);
        }

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

    internal Task<NuGetSettingsInfo> GetNuGetSettingsAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceIdentityKey = SourceIdentityKeyFactory();
        if (sourceIdentityKey.Length != NuGetSourceIdentity.KeySizeInBytes)
        {
            throw new InvalidOperationException(
                $"The NuGet source identity key must be {NuGetSourceIdentity.KeySizeInBytes} bytes.");
        }

        return Task.FromResult(_nuGetClient.GetSettings(workingDirectory, sourceIdentityKey));
    }

    internal Task WriteNuGetConfigOverlayAsync(
        NuGetConfigOverlayRequest overlay,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        _nuGetClient.WriteConfigOverlay(overlay, outputPath);
        return Task.CompletedTask;
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

    /// <summary>
    /// Gets the file containing the NuGet implementation that performs restores, for the restore cache key.
    /// </summary>
    /// <remarks>
    /// Native AOT compiles the implementation into the executable. A managed launch such as <c>dotnet aspire.dll</c>
    /// runs it from the CLI assembly instead, and <see cref="Environment.ProcessPath"/> is then the <c>dotnet</c> host,
    /// which does not change when the CLI is updated.
    /// </remarks>
    internal static string? GetRestoreToolPath()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return Environment.ProcessPath;
        }

        var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(BundleNuGetService).Assembly.GetName().Name}.dll");
        return File.Exists(assemblyPath) ? assemblyPath : Environment.ProcessPath;
    }

    internal static string ComputePackageHash(
        List<(string Id, string Version)> packages,
        string tfm,
        string? runtimeIdentifier,
        string? toolPath = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigCacheIdentity = null,
        string? nugetPackagesPath = null)
    {
        var content = string.Join(";", packages.OrderBy(package => package.Id).Select(package => $"{package.Id}:{package.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";tool:{GetToolFingerprint(toolPath)}";
        if (sources is not null)
        {
            foreach (var source in sources.OrderBy(static source => source, StringComparer.OrdinalIgnoreCase))
            {
                content += $";source:{source.Length}:{source}";
            }
        }
        if (nugetConfigCacheIdentity is not null)
        {
            content += $";config:{nugetConfigCacheIdentity}";
        }
        if (nugetPackagesPath is not null)
        {
            content += $";global-packages:{nugetPackagesPath.Length}:{nugetPackagesPath}";
        }

        return XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(content)).ToString("X16", CultureInfo.InvariantCulture);
    }

    private static string? ComputeNuGetConfigCacheIdentity(
        string? nugetSettingsCacheIdentity,
        string? nugetConfigOverlayCacheIdentity)
    {
        if (nugetSettingsCacheIdentity is null && nugetConfigOverlayCacheIdentity is null)
        {
            return null;
        }

        var hash = new XxHash3();
        if (nugetSettingsCacheIdentity is not null)
        {
            hash.Append("\0NUGET_SETTINGS\0"u8);
            hash.Append(Encoding.UTF8.GetBytes(nugetSettingsCacheIdentity));
        }
        if (nugetConfigOverlayCacheIdentity is not null)
        {
            hash.Append("\0NUGET_CONFIG_OVERLAY\0"u8);
            hash.Append(Encoding.UTF8.GetBytes(nugetConfigOverlayCacheIdentity));
        }

        return Convert.ToHexString(hash.GetCurrentHash());
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
