// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Service for NuGet operations that works in bundle mode.
/// Uses the NuGetHelper tool via the layout runtime.
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
    /// <param name="globalPackagesFolderOverride">An optional global packages folder override for the restore process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The package probe manifest and ownership of any temporary restore artifacts.</returns>
    Task<PackageRestoreResult> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        string? globalPackagesFolderOverride = null,
        CancellationToken ct = default);
}

/// <summary>
/// NuGet service implementation that uses the bundle's NuGetHelper tool.
/// </summary>
internal sealed class BundleNuGetService : INuGetService
{
    internal const string TemporaryCredentialRestoreDirectoryName = "temporary";
    internal const string TemporaryCredentialRestoreDirectoryPrefix = "credential";

    private readonly ILayoutDiscovery _layoutDiscovery;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly IFeatures _features;
    private readonly IEnvironment _environment;
    private readonly ILogger<BundleNuGetService> _logger;
    private readonly IBundleService? _bundleService;

    public BundleNuGetService(
        ILayoutDiscovery layoutDiscovery,
        LayoutProcessRunner layoutProcessRunner,
        IFeatures features,
        IEnvironment environment,
        ILogger<BundleNuGetService> logger,
        IBundleService? bundleService = null)
    {
        _layoutDiscovery = layoutDiscovery;
        _layoutProcessRunner = layoutProcessRunner;
        _features = features;
        _environment = environment;
        _logger = logger;
        _bundleService = bundleService;
    }

    public async Task<PackageRestoreResult> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        string? globalPackagesFolderOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var layoutLease = _bundleService is null
            ? null
            : await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "nuget-restore", ct).ConfigureAwait(false);
        var layout = layoutLease?.Layout ?? _layoutDiscovery.DiscoverLayout();
        if (layout is null)
        {
            throw new InvalidOperationException("Bundle layout not found. Cannot perform NuGet restore in bundle mode.");
        }

        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        var packageList = packages.ToList();
        if (packageList.Count == 0)
        {
            throw new ArgumentException("At least one package is required", nameof(packages));
        }

        var sourceList = sources?.ToArray();
        var nugetConfigInspection = await InspectNuGetConfigAsync(nugetConfigPath, ct).ConfigureAwait(false);
        var sensitiveSources = sourceList?
            .Where(PackageSourceOverrideMappings.HasCredentialMaterial)
            .Concat(nugetConfigInspection.CredentialBearingSources)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? nugetConfigInspection.CredentialBearingSources;
        var containsCredentialMaterial =
            nugetConfigInspection.ContainsCredentialMaterial ||
            sensitiveSources.Length > 0;
        var nugetFallbackPackagesPaths = CliPathHelper.GetNuGetFallbackPackagesEnvironmentPaths(_environment);

        TemporaryCacheDirectory? temporaryRestoreDirectory = null;
        var restoreDir = containsCredentialMaterial
            ? CreateTemporaryCredentialRestoreDirectory(workingDirectory, out temporaryRestoreDirectory)
            : Path.Combine(
                GetPackageRestoreCacheDirectory(workingDirectory),
                ComputePackageHash(
                    packageList,
                    targetFramework,
                    runtimeIdentifier,
                    managedPath,
                    sourceList,
                    nugetConfigInspection.CacheIdentity,
                    globalPackagesFolderOverride ?? CliPathHelper.GetNuGetPackagesEnvironmentPath(_environment),
                    nugetFallbackPackagesPaths));
        var objDir = Path.Combine(restoreDir, "obj");
        var manifestPath = Path.Combine(restoreDir, IntegrationPackageProbeManifest.FileName);
        var assetsPath = Path.Combine(objDir, "project.assets.json");
        var lockPath = Path.Combine(restoreDir, "restore.lock");

        try
        {
            // Credential-backed restores have a unique directory and do not need cross-process
            // serialization. Avoiding a lock also lets failure cleanup remove the whole directory.
            // Reusable package caches are shared by every AppHost in the workspace and must remain
            // serialized while their manifest or project.assets.json file is being written.
            using var fileLock = containsCredentialMaterial
                ? null
                : await FileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);

            // Check if already restored after acquiring the lock because another process may
            // have populated the shared cache while this process was waiting.
            if (File.Exists(manifestPath) && TryValidatePackageManifest(manifestPath, _logger))
            {
                _logger.LogDebug("Using cached package manifest at {Path}", manifestPath);
                var cachedResult = new PackageRestoreResult(manifestPath, temporaryRestoreDirectory);
                temporaryRestoreDirectory = null;
                return cachedResult;
            }

            Directory.CreateDirectory(objDir);

            // Step 1: Restore packages
            // Prepend "nuget" subcommand for aspire-managed dispatch
            var restoreArgs = new List<string>
        {
            "nuget",
            "restore",
            "--output", objDir,
            "--framework", targetFramework
        };

            if (!string.IsNullOrEmpty(runtimeIdentifier))
            {
                restoreArgs.Add("--runtime-identifier");
                restoreArgs.Add(runtimeIdentifier);
            }

            foreach (var (id, version) in packageList)
            {
                restoreArgs.Add("--package");
                restoreArgs.Add($"{id},{version}");
            }

            if (sourceList is not null)
            {
                foreach (var source in sourceList)
                {
                    restoreArgs.Add("--source");
                    restoreArgs.Add(source);
                }
            }

            // Pass working directory for nuget.config discovery.
            restoreArgs.Add("--working-dir");
            restoreArgs.Add(workingDirectory);

            if (!string.IsNullOrEmpty(nugetConfigPath))
            {
                restoreArgs.Add("--nuget-config");
                restoreArgs.Add(nugetConfigPath);
            }

            // Enable verbose output for debugging
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                restoreArgs.Add("--verbose");
            }

            _logger.LogDebug("Restoring {Count} packages", packageList.Count);
            _logger.LogDebug("aspire-managed path: {ManagedPath}", managedPath);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                // Build a redacted copy of the args specifically for the log line so user-supplied
                // credentialed feeds (e.g., `https://user:pat@host/v3/index.json`, SAS-token URLs) do
                // not flow to the debug log alongside the rest of the restore invocation. The
                // original `restoreArgs` list is still passed verbatim to the process below.
                _logger.LogDebug("NuGet restore args: {Args}", string.Join(" ", BuildRedactedArgsForLog(restoreArgs)));
            }

            var environmentVariables = new Dictionary<string, string>();
            string? effectiveGlobalPackagesFolder;
            if (containsCredentialMaterial)
            {
                // NuGet persists each package's source URL in .nupkg.metadata. Keep that metadata
                // under the same lease as credential-bearing restore artifacts so a user-info or
                // token-bearing URL cannot survive after the AppHost releases the manifest.
                effectiveGlobalPackagesFolder = Path.Combine(restoreDir, "packages");
            }
            else
            {
                effectiveGlobalPackagesFolder = globalPackagesFolderOverride;
            }

            if (effectiveGlobalPackagesFolder is not null)
            {
                environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] = effectiveGlobalPackagesFolder;
            }
            NuGetSignatureVerificationEnabler.Apply(environmentVariables, _features, _environment);
            layoutLease?.AddEnvironment(environmentVariables);

            var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            restoreArgs,
            environmentVariables: environmentVariables,
            // A restore against a slow/unresponsive NuGet source can hang. LayoutProcessRunner uses this
            // to bind the helper to the CLI's Windows kill-on-close job (and, on non-Windows, to instead
            // arm the cooperative parent-liveness watchdog) so a hard-killed CLI cannot leak it.
            killOnParentExit: true,
            ct: ct);

            var redactedError = PackageSourceRedactor.RedactOccurrences(error, sensitiveSources);
            var redactedOutput = PackageSourceRedactor.RedactOccurrences(output, sensitiveSources);

            // NuGet errors often repeat the feed URL. Redact helper output separately from the
            // invocation arguments so SAS tokens and URL user-info cannot reach logs or exceptions.
            if (!string.IsNullOrWhiteSpace(redactedError))
            {
                _logger.LogDebug("NuGetHelper restore stderr: {Error}", redactedError);
            }

            if (exitCode != 0)
            {
                _logger.LogError("Package restore failed with exit code {ExitCode}", exitCode);
                _logger.LogError("Package restore stderr: {Error}", redactedError);
                _logger.LogError("Package restore stdout: {Output}", redactedOutput);
                throw new InvalidOperationException($"Package restore failed: {redactedError}");
            }

            // Step 2: Create package probe manifest
            // Prepend "nuget" subcommand for aspire-managed dispatch
            var manifestArgs = new List<string>
        {
            "nuget",
            "manifest",
            "--assets", assetsPath,
            "--output", manifestPath,
            "--framework", targetFramework
        };

            if (!string.IsNullOrEmpty(runtimeIdentifier))
            {
                manifestArgs.Add("--runtime-identifier");
                manifestArgs.Add(runtimeIdentifier);
            }

            // Enable verbose output for debugging
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                manifestArgs.Add("--verbose");
            }

            _logger.LogDebug("Creating package manifest from {AssetsPath}", assetsPath);
            _logger.LogDebug("NuGet manifest args: {Args}", string.Join(" ", manifestArgs));

            (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            manifestArgs,
            environmentVariables: environmentVariables,
            // Same rationale as the restore step above: keep this aspire-managed helper from outliving a
            // hard-killed CLI (Windows kill-on-close job, or the cooperative watchdog on other hosts).
            killOnParentExit: true,
            ct: ct);

            redactedError = PackageSourceRedactor.RedactOccurrences(error, sensitiveSources);
            redactedOutput = PackageSourceRedactor.RedactOccurrences(output, sensitiveSources);
            if (!string.IsNullOrWhiteSpace(redactedError))
            {
                _logger.LogDebug("NuGetHelper manifest stderr: {Error}", redactedError);
            }

            if (exitCode != 0)
            {
                _logger.LogError("Manifest creation failed with exit code {ExitCode}", exitCode);
                _logger.LogError("Manifest creation stderr: {Error}", redactedError);
                _logger.LogError("Manifest creation stdout: {Output}", redactedOutput);
                throw new InvalidOperationException($"Manifest creation failed: {redactedError}");
            }

            _logger.LogDebug("Package manifest created at {Path}", manifestPath);
            var restoreResult = new PackageRestoreResult(manifestPath, temporaryRestoreDirectory);
            temporaryRestoreDirectory = null;
            return restoreResult;
        }
        finally
        {
            temporaryRestoreDirectory?.Dispose();
        }
    }

    internal async Task<string[]> GetNuGetConfigPathsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var layoutLease = _bundleService is null
            ? null
            : await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "nuget-config-paths", cancellationToken).ConfigureAwait(false);
        var layout = layoutLease?.Layout ?? _layoutDiscovery.DiscoverLayout();
        var managedPath = layout?.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            ["nuget", "config-paths", "--working-dir", workingDirectory],
            killOnParentExit: true,
            ct: cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Unable to discover the NuGet configuration hierarchy for '{workingDirectory}': {error}");
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidDataException("The NuGet configuration hierarchy response was not an array.");
            }

            return document.RootElement
                .EnumerateArray()
                .Select(static element => element.GetString()
                    ?? throw new InvalidDataException("The NuGet configuration hierarchy contained a null path."))
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The NuGet configuration hierarchy response was invalid.", ex);
        }
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

    // Returns a redacted copy of the restore args suitable for debug logging. Replaces the value
    // immediately following each `--source` token with the credential-safe form from
    // PackageSourceRedactor. Built defensively to handle repeated `--source` flags and a missing
    // trailing value at the end of the args list.
    private static IReadOnlyList<string> BuildRedactedArgsForLog(IReadOnlyList<string> args)
    {
        var redacted = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            redacted.Add(args[i]);
            if (string.Equals(args[i], "--source", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                redacted.Add(PackageSourceRedactor.RedactForDisplay(args[++i]));
            }
        }

        return redacted;
    }

    internal static string ComputePackageHash(
        List<(string Id, string Version)> packages,
        string tfm,
        string? runtimeIdentifier,
        string? managedPath = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigCacheIdentity = null,
        string? nugetPackagesPath = null,
        IReadOnlyList<string>? nugetFallbackPackagesPaths = null)
    {
        var content = string.Join(";", packages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";managed:{GetManagedToolFingerprint(managedPath)}";
        if (sources is not null)
        {
            content += $";sources:{string.Join("|", sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}";
        }
        if (nugetConfigCacheIdentity is not null)
        {
            content += $";config:{nugetConfigCacheIdentity}";
        }
        if (nugetPackagesPath is not null)
        {
            content += $";global-packages:{nugetPackagesPath}";
        }
        if (nugetFallbackPackagesPaths is not null)
        {
            foreach (var path in nugetFallbackPackagesPaths)
            {
                content += $";fallback-packages:{path.Length}:{path}";
            }
        }

        // Use SHA256 for stable hash across processes/runtimes
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes)[..16]; // Use first 16 chars (64 bits) for reasonable uniqueness
    }

    private string CreateTemporaryCredentialRestoreDirectory(
        string workingDirectory,
        out TemporaryCacheDirectory temporaryRestoreDirectory)
    {
        var temporaryRoot = Path.Combine(
            GetPackageRestoreCacheDirectory(workingDirectory),
            TemporaryCredentialRestoreDirectoryName);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(temporaryRoot);
        CleanupAbandonedTemporaryCredentialRestoreDirectories(temporaryRoot);
        temporaryRestoreDirectory = TemporaryCacheDirectory.Create(
            temporaryRoot,
            TemporaryCredentialRestoreDirectoryPrefix,
            path => TryDeleteDirectory(path, _logger),
            path => TryDeleteFile(path, _logger));
        return temporaryRestoreDirectory.FullName;
    }

    private void CleanupAbandonedTemporaryCredentialRestoreDirectories(string temporaryRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
            temporaryRoot,
            $".{TemporaryCredentialRestoreDirectoryPrefix}-*"))
        {
            try
            {
                var leasePath = TemporaryCacheDirectory.GetLeasePath(directory);
                using (TemporaryCacheDirectory.OpenLease(directory))
                {
                    TryDeleteDirectory(directory, _logger);
                }

                TryDeleteFile(leasePath, _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Unable to clean temporary package restore directory {Path}; it may still be in use.", directory);
            }
        }
    }

    private static async Task<NuGetConfigInspection> InspectNuGetConfigAsync(string? nugetConfigPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nugetConfigPath))
        {
            return NuGetConfigInspection.Empty;
        }

        await using var stream = new FileStream(
            nugetConfigPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);

        var containsCredentialMaterial = TemporaryNuGetConfig.DocumentContainsCredentialMaterial(document);
        var credentialBearingSources = TemporaryNuGetConfig.GetCredentialBearingSources(document);

        return new NuGetConfigInspection(
            containsCredentialMaterial ? null : document.ToString(SaveOptions.DisableFormatting),
            containsCredentialMaterial,
            credentialBearingSources);
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Unable to remove temporary package restore directory {Path}.", path);
        }
    }

    private static void TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Unable to remove temporary package restore lease {Path}.", path);
        }
    }

    private static string GetManagedToolFingerprint(string? managedPath)
    {
        if (string.IsNullOrEmpty(managedPath))
        {
            return "<none>";
        }

        try
        {
            var fileInfo = new FileInfo(managedPath);
            if (!fileInfo.Exists)
            {
                return "<missing>";
            }

            return $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return "<error>";
        }
        catch (UnauthorizedAccessException)
        {
            return "<error>";
        }
        catch (NotSupportedException)
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

    private sealed record NuGetConfigInspection(
        string? CacheIdentity,
        bool ContainsCredentialMaterial,
        string[] CredentialBearingSources)
    {
        public static NuGetConfigInspection Empty { get; } = new(null, false, []);
    }
}

internal sealed class PackageRestoreResult(string manifestPath, TemporaryCacheDirectory? temporaryDirectory) : IDisposable
{
    private TemporaryCacheDirectory? _temporaryDirectory = temporaryDirectory;

    public string ManifestPath { get; } = manifestPath;

    public bool IsTemporary => _temporaryDirectory is not null;

    public void Dispose()
    {
        Interlocked.Exchange(ref _temporaryDirectory, null)?.Dispose();
    }
}
