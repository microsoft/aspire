// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Layout;

/// <summary>
/// Known layout component types.
/// </summary>
public enum LayoutComponent
{
    /// <summary>CLI executable.</summary>
    Cli,
    /// <summary>Developer Control Plane.</summary>
    Dcp,
    /// <summary>Unified managed binary (dashboard, server, nuget).</summary>
    Managed,
    /// <summary>Bundled watch tool (Microsoft.DotNet.HotReload.Watch.Aspire).</summary>
    Watch
}

/// <summary>
/// Configuration for the Aspire bundle layout.
/// Specifies paths to all components in a self-contained bundle.
/// </summary>
public sealed class LayoutConfiguration
{
    /// <summary>
    /// Bundle version (e.g., "13.2.0" or "dev" for local development).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Target platform (e.g., "linux-x64", "win-x64").
    /// </summary>
    public string? Platform { get; set; }

    /// <summary>
    /// Root path of the layout.
    /// </summary>
    public string? LayoutPath { get; set; }

    /// <summary>
    /// Component paths relative to LayoutPath.
    /// </summary>
    public LayoutComponents Components { get; set; } = new();

    /// <summary>
    /// List of integrations included in the bundle.
    /// </summary>
    public List<string> BuiltInIntegrations { get; set; } = [];

    /// <summary>
    /// Gets the absolute path to a component.
    /// </summary>
    public string? GetComponentPath(LayoutComponent component)
    {
        if (string.IsNullOrEmpty(LayoutPath))
        {
            return null;
        }

        var relativePath = component switch
        {
            LayoutComponent.Cli => Components.Cli,
            LayoutComponent.Dcp => Components.Dcp,
            LayoutComponent.Managed => Components.Managed,
            LayoutComponent.Watch => Components.Watch,
            _ => null
        };

        return relativePath is not null ? Path.Combine(LayoutPath, relativePath) : null;
    }

    /// <summary>
    /// Gets the path to the DCP directory.
    /// </summary>
    public string? GetDcpPath() => GetComponentPath(LayoutComponent.Dcp);

    /// <summary>
    /// Gets the path to the aspire-managed executable.
    /// </summary>
    /// <returns>The path to aspire-managed(.exe).</returns>
    public string? GetManagedPath()
    {
        var managedDir = GetComponentPath(LayoutComponent.Managed);
        if (managedDir is null)
        {
            return null;
        }

        return Path.Combine(managedDir, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
    }

    /// <summary>
    /// Gets the path to the bundled watch tool DLL.
    /// </summary>
    public string? GetWatchToolPath()
    {
        var watchDir = GetComponentPath(LayoutComponent.Watch);
        if (watchDir is null)
        {
            return null;
        }

        // Watch is a mandatory bundle component for shipping CLI but may not be present 
        // in externally-supplied or legacy layouts. 
        var watchToolPath = Path.Combine(watchDir, BundleDiscovery.WatchToolDllName);
        return File.Exists(watchToolPath) ? watchToolPath : null;
    }
}

/// <summary>
/// Component paths within the layout.
/// </summary>
public sealed class LayoutComponents
{
    /// <summary>
    /// Path to CLI executable (e.g., "aspire" or "aspire.exe").
    /// </summary>
    public string? Cli { get; set; } = "aspire";

    /// <summary>
    /// Path to Developer Control Plane.
    /// </summary>
    public string? Dcp { get; set; } = BundleDiscovery.DcpDirectoryName;

    /// <summary>
    /// Path to the unified managed binary directory.
    /// </summary>
    public string? Managed { get; set; } = BundleDiscovery.ManagedDirectoryName;

    /// <summary>
    /// Path to the bundled watch tool directory.
    /// </summary>
    public string? Watch { get; set; } = BundleDiscovery.WatchDirectoryName;
}
