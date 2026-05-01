// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Service for NuGet operations that works in bundle mode.
/// Uses the NuGetHelper tool via the layout runtime.
/// </summary>
public interface INuGetService
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
    /// <returns>The package restore result.</returns>
    Task<NuGetPackageRestoreResult> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        CancellationToken ct = default);
}

/// <summary>
/// Represents restored NuGet package metadata for the prebuilt AppHost server.
/// </summary>
/// <param name="ManifestPath">The path to the package probe manifest.</param>
public sealed record NuGetPackageRestoreResult(string ManifestPath);

/// <summary>
/// NuGet service implementation that uses the bundle's NuGetHelper tool.
/// </summary>
internal sealed class BundleNuGetService : INuGetService
{
    private readonly ILayoutDiscovery _layoutDiscovery;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly IFeatures _features;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<BundleNuGetService> _logger;

    public BundleNuGetService(
        ILayoutDiscovery layoutDiscovery,
        LayoutProcessRunner layoutProcessRunner,
        IFeatures features,
        CliExecutionContext executionContext,
        ILogger<BundleNuGetService> logger)
    {
        _layoutDiscovery = layoutDiscovery;
        _layoutProcessRunner = layoutProcessRunner;
        _features = features;
        _executionContext = executionContext;
        _logger = logger;
    }

    public async Task<NuGetPackageRestoreResult> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigPath = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var layout = _layoutDiscovery.DiscoverLayout();
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

        // Compute a hash for the package set to create a unique restore location.
        var packageHash = ComputePackageHash(packageList, targetFramework, runtimeIdentifier, managedPath, sources);
        var restoreCacheDirectory = GetPackageRestoreCacheDirectory(workingDirectory);
        var restoreDir = Path.Combine(restoreCacheDirectory, packageHash);
        var objDir = Path.Combine(restoreDir, "obj");
        var manifestPath = Path.Combine(restoreDir, IntegrationPackageProbeManifest.FileName);
        var assetsPath = Path.Combine(objDir, "project.assets.json");

        // Check if already restored
        if (File.Exists(manifestPath))
        {
            _logger.LogDebug("Using cached package manifest at {Path}", manifestPath);
            return new NuGetPackageRestoreResult(manifestPath);
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

        if (sources is not null)
        {
            foreach (var source in sources)
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
        _logger.LogDebug("NuGet restore args: {Args}", string.Join(" ", restoreArgs));

        var environmentVariables = new Dictionary<string, string>();
        NuGetSignatureVerificationEnabler.Apply(environmentVariables, _features, _executionContext);

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            restoreArgs,
            environmentVariables: environmentVariables,
            ct: ct);

        // Log stderr at debug level for diagnostics
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("NuGetHelper restore stderr: {Error}", error);
        }

        if (exitCode != 0)
        {
            _logger.LogError("Package restore failed with exit code {ExitCode}", exitCode);
            _logger.LogError("Package restore stderr: {Error}", error);
            _logger.LogError("Package restore stdout: {Output}", output);
            throw new InvalidOperationException($"Package restore failed: {error}");
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
            ct: ct);

        // Log stderr at debug level for diagnostics
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("NuGetHelper manifest stderr: {Error}", error);
        }

        if (exitCode != 0)
        {
            _logger.LogError("Manifest creation failed with exit code {ExitCode}", exitCode);
            _logger.LogError("Manifest creation stderr: {Error}", error);
            _logger.LogError("Manifest creation stdout: {Output}", output);
            throw new InvalidOperationException($"Manifest creation failed: {error}");
        }

        _logger.LogDebug("Package manifest created at {Path}", manifestPath);
        return new NuGetPackageRestoreResult(manifestPath);
    }

    internal static string ComputePackageHash(
        List<(string Id, string Version)> packages,
        string tfm,
        string? runtimeIdentifier,
        string? managedPath = null,
        IEnumerable<string>? sources = null)
    {
        var content = string.Join(";", packages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";managed:{GetManagedToolFingerprint(managedPath)}";
        if (sources is not null)
        {
            content += $";sources:{string.Join("|", sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}";
        }

        // Use SHA256 for stable hash across processes/runtimes
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes)[..16]; // Use first 16 chars (64 bits) for reasonable uniqueness
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
}
