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
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using NuGet.ProjectModel;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.NuGet;

internal sealed record NuGetSourceInfo(
    string Name,
    string Identity,
    bool IsEnabled,
    bool HasCredentials,
    bool HasClientCertificates)
{
    public bool IsCliManaged { get; init; }
}

internal sealed record NuGetPackageSourceMapping(
    string SourceKey,
    IReadOnlyList<string> Patterns);

internal sealed record NuGetSettingsInfo(
    IReadOnlyList<string> ConfigPaths,
    string CacheIdentity,
    IReadOnlyList<NuGetSourceInfo> Sources,
    IReadOnlyList<string> SensitiveSourceValues,
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
/// Orchestrates NuGet configuration and in-process package restore with reusable restore caches.
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

    internal NuGetSettingsInfo GetNuGetSettings(
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

        return _nuGetClient.GetSettings(workingDirectory, sourceIdentityKey);
    }

    internal NuGetConfiguration BuildConfiguration(
        DirectoryInfo workingDirectory,
        string workloadId,
        PackageMapping[]? selectedMappings,
        bool restrictToSelectedSources,
        string? packageScopedAppendSource = null,
        bool hasAuthoritativeAspirePolicy = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var settings = GetNuGetSettings(workingDirectory.FullName, cancellationToken);
        selectedMappings = selectedMappings?
            .Select(mapping => new PackageMapping(
                mapping.PackageFilter,
                PackageSourceIdentity.IsNamedSourceReference(mapping.Source) &&
                settings.Sources.Any(source => string.Equals(source.Name, mapping.Source, StringComparison.OrdinalIgnoreCase))
                    ? mapping.Source
                    : PackageSourceOverrideMappings.ResolveForWorkingDirectory(mapping.Source, workingDirectory)))
            .ToArray();
        return NuGetConfigurationBuilder.Build(
            settings,
            workloadId,
            selectedMappings,
            restrictToSelectedSources,
            packageScopedAppendSource,
            hasAuthoritativeAspirePolicy);
    }

    internal bool IsPackageSourceMappingEnabled(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        return GetNuGetSettings(workingDirectory.FullName, cancellationToken).PackageSourceMappings.Count > 0;
    }

    internal NuGetConfiguration BuildChannelConfiguration(
        DirectoryInfo workingDirectory,
        string workloadId,
        PackageChannel? channel,
        string? packageSourceOverride,
        string? nugetServiceIndexOverride,
        CancellationToken cancellationToken)
    {
        var mappings = string.IsNullOrWhiteSpace(packageSourceOverride)
            ? channel?.Mappings
            : PackageSourceOverrideMappings.Create(packageSourceOverride, channel, nugetServiceIndexOverride);
        var selectedMappings = mappings?
            .Where(static mapping => mapping.PackageFilter != PackageMapping.AllPackages)
            .ToArray();
        if (selectedMappings is { Length: 0 } && mappings is { Length: > 0 })
        {
            // An explicitly selected catch-all-only channel (including a transition to stable)
            // selects Aspire's source without taking ownership of unrelated dependencies.
            selectedMappings =
            [
                .. mappings.Select(static mapping => new PackageMapping(
                    PackageSourceOverrideMappings.DefaultPackagePattern, mapping.Source))
            ];
        }

        return BuildConfiguration(
            workingDirectory,
            workloadId,
            selectedMappings is { Length: > 0 } ? selectedMappings : null,
            restrictToSelectedSources: false,
            hasAuthoritativeAspirePolicy: true,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Materializes the shared desired configuration without changing ambient configuration.
    /// </summary>
    internal async Task<NuGetPackageOperationConfiguration> CreateConfigurationPreviewAsync(
        DirectoryInfo workingDirectory,
        NuGetConfiguration configuration,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        var overlay = await WriteTemporaryOverlayAsync(
            configuration,
            new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire")),
            globalPackagesFolder,
            cancellationToken).ConfigureAwait(false);
        return overlay is null
            ? NuGetPackageOperationConfiguration.Ambient(workingDirectory, configuration.Settings.CacheIdentity)
            : NuGetPackageOperationConfiguration.FromTemporaryOverlay(
                workingDirectory,
                overlay,
                configuration.Settings.CacheIdentity);
    }

    internal string GetGlobalPackagesFolder(DirectoryInfo workingDirectory)
        => _nuGetClient.GetGlobalPackagesFolder(workingDirectory.FullName);

    /// <summary>
    /// Acquires an exact package without reusing an asset-only restore manifest.
    /// </summary>
    internal async Task AcquirePackageAsync(
        (string Id, string Version) package,
        NuGetPackageOperationConfiguration configuration,
        string globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-package-acquisition-");
        try
        {
            var settings = GetNuGetSettings(configuration.EffectiveWorkingDirectory.FullName, cancellationToken);
            await _nuGetClient.AcquirePackageAsync(
                package, directory.FullName,
                configuration.EffectiveWorkingDirectory.FullName, globalPackagesFolder,
                settings.SensitiveSourceValues, cancellationToken);
        }
        catch (NuGetOperationException exception)
        {
            // Native NuGet diagnostics already redact the selected policy's sensitive sources.
            _logger.LogError("Package acquisition failed: {Output}", exception.Output);
            throw new InvalidOperationException($"Package acquisition failed: {exception.Output}", exception);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not remove package acquisition directory {Directory}", directory.FullName);
            }
        }
    }

    /// <summary>
    /// Previews one configuration-file replacement in the caller's native hierarchy.
    /// </summary>
    internal async Task<NuGetPackageOperationConfiguration> CreateConfigurationPreviewAsync(
        DirectoryInfo workingDirectory, FileInfo targetFile, FileInfo replacementFile,
        CancellationToken cancellationToken)
    {
        var settings = GetNuGetSettings(workingDirectory.FullName, cancellationToken);
        var paths = settings.ConfigPaths.ToList();
        var targetPath = PathNormalizer.ResolveToFilesystemPath(targetFile.FullName);
        var index = paths.FindIndex(path => string.Equals(
            PathNormalizer.ResolveToFilesystemPath(path), targetPath, StringComparisons.FileSystemPath));
        if (index >= 0)
        {
            paths[index] = replacementFile.FullName;
        }
        else
        {
            if (!IsAncestorDirectory(targetFile.DirectoryName!, workingDirectory.FullName))
            {
                throw new InvalidOperationException(
                    $"NuGet configuration '{targetFile.FullName}' is not in the hierarchy of '{workingDirectory.FullName}'.");
            }
            index = paths.FindIndex(path =>
                IsAncestorDirectory(Path.GetDirectoryName(path)!, targetFile.DirectoryName!) ||
                !IsAncestorDirectory(Path.GetDirectoryName(path)!, workingDirectory.FullName));
            paths.Insert(index < 0 ? paths.Count : index, replacementFile.FullName);
        }

        var overlay = await TemporaryNuGetConfigFile.CreateAsync(
            new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire")),
            path => _nuGetClient.WriteNuGetConfig(paths, path));
        return NuGetPackageOperationConfiguration.FromTemporaryOverlay(
            workingDirectory, overlay, settings.CacheIdentity);
    }

    internal static bool IsAncestorDirectory(string ancestor, string directory)
    {
        var relativePath = Path.GetRelativePath(
            PathNormalizer.ResolveToFilesystemPath(ancestor), PathNormalizer.ResolveToFilesystemPath(directory));
        return !Path.IsPathRooted(relativePath) && relativePath != ".." &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparisons.FileSystemPath);
    }

    internal async Task<NuGetPackageOperationConfiguration> CreatePackageOperationConfigurationAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? mappings,
        bool restrictToSelectedSources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (restrictToSelectedSources && mappings is not { Count: > 0 })
        {
            throw new ArgumentException("Source-restricted discovery requires a selected source.", nameof(mappings));
        }

        // A channel's synthetic '*' fallback belongs to standalone configuration generation,
        // not an inherited operation. Explicit-source operations retain their selected catch-all.
        var selectedMappings = mappings?
            .Where(mapping => restrictToSelectedSources || mapping.PackageFilter != PackageMapping.AllPackages)
            .ToArray() ?? [];
        var configuration = BuildConfiguration(
            workingDirectory,
            workloadId: "package-search",
            selectedMappings.Length == 0 ? null : selectedMappings,
            restrictToSelectedSources,
            hasAuthoritativeAspirePolicy: true,
            cancellationToken: cancellationToken);

        if (configuration.Overlay is null)
        {
            return NuGetPackageOperationConfiguration.Ambient(workingDirectory, configuration.Settings.CacheIdentity);
        }

        var temporaryConfig = await WriteTemporaryOverlayAsync(
            configuration,
            workingDirectory,
            globalPackagesFolder: null,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected package mappings did not produce a NuGet policy overlay.");

        return NuGetPackageOperationConfiguration.FromTemporaryOverlay(
            workingDirectory,
            temporaryConfig,
            configuration.Settings.CacheIdentity);
    }

    internal IReadOnlyList<NuGetPackage> FilterPackageSearchResults(
        IReadOnlyList<NuGetPackage> packages,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _nuGetClient.FilterPackageSearchResults(packages, nugetConfigPath, workingDirectory);
    }

    internal async Task<TemporaryNuGetConfigFile?> WriteTemporaryOverlayAsync(
        NuGetConfiguration configuration,
        DirectoryInfo? parentDirectory,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        if (configuration.Overlay is not { } overlay)
        {
            return null;
        }

        overlay = overlay with { GlobalPackagesFolder = globalPackagesFolder };
        return parentDirectory is null
            ? await TemporaryNuGetConfigFile.CreateAsync(
                path => WriteNuGetConfig(overlay, path, cancellationToken)).ConfigureAwait(false)
            : await TemporaryNuGetConfigFile.CreateAsync(
                parentDirectory,
                path => WriteNuGetConfig(overlay, path, cancellationToken)).ConfigureAwait(false);
    }

    internal void WriteNuGetConfig(
        NuGetConfiguration configuration,
        string outputPath,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        var overlay = configuration.Overlay
            ?? throw new InvalidOperationException("The resolved configuration does not require a NuGet policy overlay.");
        WriteNuGetConfig(overlay with { GlobalPackagesFolder = globalPackagesFolder }, outputPath, cancellationToken);
    }

    internal async Task<byte[]> CreateNuGetConfigContentAsync(
        NuGetConfiguration configuration,
        ReadOnlyMemory<byte>? originalContent,
        string? globalPackagesFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var content = configuration.Overlay
            ?? throw new InvalidOperationException("The resolved configuration does not require a NuGet configuration change.");
        cancellationToken.ThrowIfCancellationRequested();

        // NuGet's typed settings writer is file-backed. Stage serialization outside the
        // workspace; the same writer preserves unrelated sections when a baseline is supplied.
        using var file = await TemporaryNuGetConfigFile.CreateAsync(
            path => _nuGetClient.WriteNuGetConfig(
                content with { GlobalPackagesFolder = globalPackagesFolder },
                path,
                originalContent)).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(file.ConfigFile.FullName, cancellationToken).ConfigureAwait(false);
    }

    internal static string CombineCacheIdentities(string configurationIdentity, string overlayIdentity)
    {
        var builder = new StringBuilder();
        foreach (var value in new[] { "SOURCE_POLICY", configurationIdentity, "OVERLAY", overlayIdentity })
        {
            builder.Append(value.Length);
            builder.Append(':');
            builder.Append(value);
        }

        return builder.ToString();
    }

    internal void WriteNuGetConfig(
        NuGetConfigOverlay overlay,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        _nuGetClient.WriteNuGetConfig(overlay, outputPath);
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
