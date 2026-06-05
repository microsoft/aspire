// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Layout;

/// <summary>
/// Service for discovering and loading Aspire bundle layouts.
/// Uses priority-based resolution: environment variables > relative paths from CLI location.
/// </summary>
public interface ILayoutDiscovery
{
    /// <summary>
    /// Attempts to discover a valid layout configuration.
    /// </summary>
    /// <param name="projectDirectory">Optional project directory (unused, kept for API compatibility).</param>
    /// <returns>Layout configuration if found and valid, null otherwise.</returns>
    LayoutConfiguration? DiscoverLayout(string? projectDirectory = null);

    /// <summary>
    /// Gets the path to a specific component, checking environment variable overrides first.
    /// </summary>
    string? GetComponentPath(LayoutComponent component, string? projectDirectory = null);

    /// <summary>
    /// Checks if bundle mode is available and should be used.
    /// </summary>
    bool IsBundleModeAvailable(string? projectDirectory = null);
}

/// <summary>
/// Implementation of layout discovery with priority-based resolution.
/// </summary>
public sealed class LayoutDiscovery : ILayoutDiscovery
{
    private readonly ILogger<LayoutDiscovery> _logger;

    public LayoutDiscovery(ILogger<LayoutDiscovery> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Overrides <see cref="Environment.ProcessPath"/> for relative-layout discovery.
    /// Used in tests to simulate the CLI executable living at an arbitrary path.
    /// </summary>
    internal string? ProcessPathOverride { get; init; }

    public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null)
    {
        // 1. Try environment variable for layout path
        var envLayoutPath = Environment.GetEnvironmentVariable(BundleDiscovery.LayoutPathEnvVar);
        if (!string.IsNullOrEmpty(envLayoutPath))
        {
            _logger.LogDebug("Found ASPIRE_LAYOUT_PATH: {Path}", envLayoutPath);
            var config = TryLoadLayoutFromPath(envLayoutPath);
            if (config is not null)
            {
                return LogEnvironmentOverrides(config);
            }
        }

        // 2. Try relative paths from CLI executable
        var relativeLayout = TryDiscoverRelativeLayout();
        if (relativeLayout is not null)
        {
            _logger.LogDebug("Discovered layout relative to CLI: {Path}", relativeLayout.LayoutPath);
            return LogEnvironmentOverrides(relativeLayout);
        }

        // 3. Try the Aspire home directory. This is the auto-extract destination
        // for sidecar-less installs (e.g. CLI binaries in read-only locations
        // like a Nix store), so the bundle the CLI just extracted has to be
        // discoverable here too — otherwise post-extract validation fails and
        // every command that depends on the bundle reports extraction failed.
        // Keep this as the last probe so colocated installs (winget, brew,
        // dotnet-tool, script, pr, localhive) are never shadowed by a stale
        // home-directory layout.
        var aspireHomeLayout = TryDiscoverAspireHomeLayout();
        if (aspireHomeLayout is not null)
        {
            _logger.LogDebug("Discovered layout in Aspire home: {Path}", aspireHomeLayout.LayoutPath);
            return LogEnvironmentOverrides(aspireHomeLayout);
        }

        _logger.LogDebug("No bundle layout discovered");
        return null;
    }

    private LayoutConfiguration? TryDiscoverAspireHomeLayout()
    {
        string aspireHome;
        try
        {
            aspireHome = CliPathHelper.GetDefaultAspireHomeDirectory();
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            _logger.LogDebug(ex, "TryDiscoverAspireHomeLayout: could not resolve Aspire home directory");
            return null;
        }

        if (string.IsNullOrEmpty(aspireHome) || !Directory.Exists(aspireHome))
        {
            return null;
        }

        _logger.LogDebug("TryDiscoverAspireHomeLayout: Checking Aspire home {Path}...", aspireHome);
        return TryInferLayout(aspireHome);
    }

    public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null)
    {
        // Check environment variable overrides first
        var envPath = component switch
        {
            LayoutComponent.Dcp => Environment.GetEnvironmentVariable(BundleDiscovery.DcpPathEnvVar),
            LayoutComponent.Managed => Environment.GetEnvironmentVariable(BundleDiscovery.ManagedPathEnvVar),
            _ => null
        };

        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
        {
            return envPath;
        }

        // Fall back to layout configuration
        var layout = DiscoverLayout(projectDirectory);
        return layout?.GetComponentPath(component);
    }

    public bool IsBundleModeAvailable(string? projectDirectory = null)
    {
        // Check if user explicitly wants SDK mode
        var useSdk = Environment.GetEnvironmentVariable(BundleDiscovery.UseGlobalDotNetEnvVar);
        if (string.Equals(useSdk, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(useSdk, "1", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("SDK mode forced via {EnvVar}", BundleDiscovery.UseGlobalDotNetEnvVar);
            return false;
        }

        var layout = DiscoverLayout(projectDirectory);
        if (layout is null)
        {
            return false;
        }

        // Validate that essential components exist
        return ValidateLayout(layout);
    }

    private LayoutConfiguration? TryLoadLayoutFromPath(string layoutPath)
    {
        _logger.LogDebug("TryLoadLayoutFromPath: {Path}", layoutPath);

        if (!Directory.Exists(layoutPath))
        {
            _logger.LogDebug("Layout path does not exist: {Path}", layoutPath);
            return null;
        }

        _logger.LogDebug("Layout path exists, checking directory structure...");

        // Log directory contents for debugging
        try
        {
            var entries = Directory.GetFileSystemEntries(layoutPath).Select(Path.GetFileName).ToArray();
            _logger.LogDebug("Layout directory contents: {Contents}", string.Join(", ", entries));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not list directory contents: {Error}", ex.Message);
        }

        // Infer layout from directory structure (well-known relative paths)
        return TryInferLayout(layoutPath);
    }

    private LayoutConfiguration? TryDiscoverRelativeLayout()
    {
        var cliPath = ProcessPathOverride ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(cliPath))
        {
            _logger.LogDebug("TryDiscoverRelativeLayout: ProcessPath is null or empty");
            return null;
        }

        var resolvedCliPath = CliPathHelper.ResolveSymlinkOrOriginalPath(cliPath, _logger);
        if (!string.Equals(resolvedCliPath, cliPath, StringComparison.Ordinal))
        {
            _logger.LogDebug("TryDiscoverRelativeLayout: Resolved CLI path {RawPath} -> {ResolvedPath}", cliPath, resolvedCliPath);

            var resolvedLayout = TryDiscoverRelativeLayout(resolvedCliPath);
            if (resolvedLayout is not null)
            {
                return resolvedLayout;
            }

            _logger.LogDebug("TryDiscoverRelativeLayout: No layout found relative to resolved CLI path; trying raw path {Path}.", cliPath);
        }

        return TryDiscoverRelativeLayout(cliPath);
    }

    private LayoutConfiguration? TryDiscoverRelativeLayout(string cliPath)
    {
        var cliDir = Path.GetDirectoryName(cliPath);
        if (string.IsNullOrEmpty(cliDir))
        {
            _logger.LogDebug("TryDiscoverRelativeLayout: Could not get directory from process path {Path}", cliPath);
            return null;
        }

        _logger.LogDebug("TryDiscoverRelativeLayout: CLI at {Path}, checking for layout...", cliDir);

        // Check if CLI is in a bundle layout
        // First, check if components are siblings of the CLI (flat layout):
        //   {layout}/aspire + {layout}/bundle/ -> {layout}/versions/{id}/
        var layout = TryInferLayout(cliDir);
        if (layout is not null)
        {
            return layout;
        }

        // Next, check the parent directory (bin/ layout where CLI is in a subdirectory):
        //   {layout}/bin/aspire + {layout}/bundle/ -> {layout}/versions/{id}/
        var parentDir = Path.GetDirectoryName(cliDir);
        if (!string.IsNullOrEmpty(parentDir))
        {
            _logger.LogDebug("TryDiscoverRelativeLayout: Checking parent directory {Path}...", parentDir);
            layout = TryInferLayout(parentDir);
            if (layout is not null)
            {
                return layout;
            }
        }

        return null;
    }

    private LayoutConfiguration? TryInferLayout(string layoutPath)
    {
        // New layout: a single bundle/ link whose target contains managed/ and dcp/.
        var bundlePath = Path.Combine(layoutPath, BundleDiscovery.BundleDirectoryName);
        var bundleManagedPath = Path.Combine(bundlePath, BundleDiscovery.ManagedDirectoryName);
        var bundleDcpPath = Path.Combine(bundlePath, BundleDiscovery.DcpDirectoryName);
        var managedExeName = BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName);

        _logger.LogDebug("TryInferLayout: Checking layout at {Path}", layoutPath);
        _logger.LogDebug("  {Dir}/{Managed}/: {Exists}", BundleDiscovery.BundleDirectoryName, BundleDiscovery.ManagedDirectoryName, Directory.Exists(bundleManagedPath) ? "exists" : "MISSING");
        _logger.LogDebug("  {Dir}/{Dcp}/: {Exists}", BundleDiscovery.BundleDirectoryName, BundleDiscovery.DcpDirectoryName, Directory.Exists(bundleDcpPath) ? "exists" : "MISSING");

        if (Directory.Exists(bundleManagedPath) && Directory.Exists(bundleDcpPath))
        {
            var bundleManagedExe = Path.Combine(bundleManagedPath, managedExeName);
            _logger.LogDebug("  {Dir}/{Managed}/{Exe}: {Exists}", BundleDiscovery.BundleDirectoryName, BundleDiscovery.ManagedDirectoryName, managedExeName, File.Exists(bundleManagedExe) ? "exists" : "MISSING");

            if (File.Exists(bundleManagedExe))
            {
                _logger.LogDebug("TryInferLayout: New bundle/ layout is valid");
                return new LayoutConfiguration
                {
                    LayoutPath = layoutPath,
                    Components = new LayoutComponents
                    {
                        Dcp = Path.Combine(BundleDiscovery.BundleDirectoryName, BundleDiscovery.DcpDirectoryName),
                        Managed = Path.Combine(BundleDiscovery.BundleDirectoryName, BundleDiscovery.ManagedDirectoryName),
                    }
                };
            }
        }

        // Legacy layout: top-level managed/ and dcp/ directories (or reparse points).
        var managedPath = Path.Combine(layoutPath, BundleDiscovery.ManagedDirectoryName);
        var dcpPath = Path.Combine(layoutPath, BundleDiscovery.DcpDirectoryName);

        _logger.LogDebug("  {Dir}/: {Exists}", BundleDiscovery.ManagedDirectoryName, Directory.Exists(managedPath) ? "exists" : "MISSING");
        _logger.LogDebug("  {Dir}/: {Exists}", BundleDiscovery.DcpDirectoryName, Directory.Exists(dcpPath) ? "exists" : "MISSING");

        if (!Directory.Exists(managedPath) || !Directory.Exists(dcpPath))
        {
            _logger.LogDebug("TryInferLayout: Layout rejected - missing required directories");
            return null;
        }

        // Check for aspire-managed executable
        var managedExePath = Path.Combine(managedPath, managedExeName);
        _logger.LogDebug("  managed/{ManagedExe}: {Exists}", managedExeName, File.Exists(managedExePath) ? "exists" : "MISSING");

        if (!File.Exists(managedExePath))
        {
            _logger.LogDebug("TryInferLayout: Layout rejected - aspire-managed not found");
            return null;
        }

        _logger.LogDebug("TryInferLayout: Legacy layout is valid");

        // Infer a basic layout configuration
        return new LayoutConfiguration
        {
            LayoutPath = layoutPath,
            Components = new LayoutComponents()
        };
    }

    private LayoutConfiguration LogEnvironmentOverrides(LayoutConfiguration config)
    {
        // Environment variables for specific components take precedence
        // These will be checked at GetComponentPath time, but we note them here for logging

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BundleDiscovery.DcpPathEnvVar)))
        {
            _logger.LogDebug("DCP path override from {EnvVar}", BundleDiscovery.DcpPathEnvVar);
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BundleDiscovery.ManagedPathEnvVar)))
        {
            _logger.LogDebug("Managed path override from {EnvVar}", BundleDiscovery.ManagedPathEnvVar);
        }

        return config;
    }

    private bool ValidateLayout(LayoutConfiguration layout)
    {
        // Check that aspire-managed exists
        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            _logger.LogDebug("Layout validation failed: aspire-managed not found at {Path}", managedPath);
            return false;
        }

        // Require DCP for valid layouts
        var dcpPath = layout.GetComponentPath(LayoutComponent.Dcp);
        if (dcpPath is null || !Directory.Exists(dcpPath))
        {
            _logger.LogDebug("Layout validation failed: DCP not found");
            return false;
        }

        return true;
    }
}
