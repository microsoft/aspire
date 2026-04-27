// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Service for NuGet operations that works in bundle mode.
/// Uses the NuGetHelper tool via the layout runtime.
/// </summary>
public interface INuGetService
{
    /// <summary>
    /// Restores packages to the cache and creates a flat layout.
    /// </summary>
    /// <param name="packages">The packages to restore.</param>
    /// <param name="targetFramework">The target framework.</param>
    /// <param name="runtimeIdentifier">The runtime identifier used to prefer runtime-specific assets in the generated layout.</param>
    /// <param name="sources">Additional NuGet sources.</param>
    /// <param name="workingDirectory">Working directory for nuget.config discovery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the restored libs directory.</returns>
    Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? workingDirectory = null,
        CancellationToken ct = default);
}

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
    private readonly string _cacheDirectory;

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
        _cacheDirectory = GetCacheDirectory();
    }

    public async Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
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

        // Compute a hash for the package set to create a unique restore location
        var packageHash = ComputePackageHash(packageList, targetFramework, runtimeIdentifier, managedPath);
        var restoreDir = Path.Combine(_cacheDirectory, "restore", packageHash);
        var objDir = Path.Combine(restoreDir, "obj");
        var libsDir = Path.Combine(restoreDir, "libs");
        var assetsPath = Path.Combine(objDir, "project.assets.json");

        // Check if already restored
        if (Directory.Exists(libsDir) && Directory.GetFiles(libsDir, "*.dll").Length > 0)
        {
            _logger.LogDebug("Using cached restore at {Path}", libsDir);
            return libsDir;
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

        // Pass working directory for nuget.config discovery
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            restoreArgs.Add("--working-dir");
            restoreArgs.Add(workingDirectory);
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

        // Step 2: Create flat layout
        // Prepend "nuget" subcommand for aspire-managed dispatch
        var layoutArgs = new List<string>
        {
            "nuget",
            "layout",
            "--assets", assetsPath,
            "--output", libsDir,
            "--framework", targetFramework
        };

        if (!string.IsNullOrEmpty(runtimeIdentifier))
        {
            layoutArgs.Add("--runtime-identifier");
            layoutArgs.Add(runtimeIdentifier);
        }

        // Enable verbose output for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            layoutArgs.Add("--verbose");
        }

        _logger.LogDebug("Creating layout from {AssetsPath}", assetsPath);
        _logger.LogDebug("NuGet layout args: {Args}", string.Join(" ", layoutArgs));

        (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            layoutArgs,
            environmentVariables: environmentVariables,
            ct: ct);

        // Log stderr at debug level for diagnostics
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("NuGetHelper layout stderr: {Error}", error);
        }

        if (exitCode != 0)
        {
            _logger.LogError("Layout creation failed with exit code {ExitCode}", exitCode);
            _logger.LogError("Layout creation stderr: {Error}", error);
            _logger.LogError("Layout creation stdout: {Output}", output);
            throw new InvalidOperationException($"Layout creation failed: {error}");
        }

        _logger.LogDebug("Packages restored to {Path}", libsDir);
        return libsDir;
    }

    internal static string ComputePackageHash(List<(string Id, string Version)> packages, string tfm, string? runtimeIdentifier, string? managedPath = null)
    {
        var content = string.Join(";", packages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";managed:{GetManagedToolFingerprint(managedPath)}";

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

    private static string GetCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aspire", "packages");
    }
}
