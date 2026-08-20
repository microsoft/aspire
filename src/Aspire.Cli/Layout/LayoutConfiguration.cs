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
    Cli = 0,
    /// <summary>Developer Control Plane.</summary>
    Dcp = 1,
    /// <summary>Unified managed binary (server, NuGet, terminal host).</summary>
    Managed = 2,
    /// <summary>Dashboard executable and static assets.</summary>
    Dashboard = 3
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
            LayoutComponent.Dashboard => Components.Dashboard,
            LayoutComponent.Managed => Components.Managed,
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
    /// Gets the path to the Native AOT Dashboard executable, falling back to the legacy unified binary.
    /// </summary>
    /// <returns>The path to the Dashboard executable.</returns>
    public string? GetDashboardPath()
    {
        // Current bundles keep the Native AOT Dashboard and its static assets isolated from
        // aspire-managed so each executable can use its own content root.
        var dashboardDir = GetComponentPath(LayoutComponent.Dashboard);
        if (dashboardDir is not null)
        {
            var dashboardPath = Path.Combine(
                dashboardDir,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.DashboardExecutableName));
            if (File.Exists(dashboardPath))
            {
                return dashboardPath;
            }
        }

        // Preserve compatibility with bundles created before dashboard/ became a separate
        // component. Transitional bundles placed Aspire.Dashboard in managed/, while older
        // bundles dispatch the "dashboard" subcommand through aspire-managed itself.
        var managedDir = GetComponentPath(LayoutComponent.Managed);
        if (managedDir is null)
        {
            return null;
        }

        var legacyDashboardPath = Path.Combine(
            managedDir,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.DashboardExecutableName));

        return File.Exists(legacyDashboardPath) ? legacyDashboardPath : GetManagedPath();
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
    /// Path to the Dashboard executable and static assets directory.
    /// </summary>
    public string? Dashboard { get; set; } = BundleDiscovery.DashboardDirectoryName;

    /// <summary>
    /// Path to the unified managed binary directory.
    /// </summary>
    public string? Managed { get; set; } = BundleDiscovery.ManagedDirectoryName;
}
