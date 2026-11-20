// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Resolves DCP and Dashboard paths from an Aspire CLI bundle layout.
/// </summary>
public sealed class ResolveAspireCliBundle : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Optional path to an Aspire CLI bundle or layout directory.
    /// </summary>
    public string? AspireCliBundlePath { get; set; }

    /// <summary>
    /// Optional path to an Aspire CLI executable.
    /// </summary>
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to search <c>PATH</c> for Aspire CLI executables.
    /// </summary>
    /// <remarks>
    /// Explicit paths and the Aspire home directory are still evaluated when this value is <see langword="false"/>.
    /// </remarks>
    public bool SearchPathForAspireCli { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether invalid explicitly configured paths produce warnings.
    /// </summary>
    public bool WarnOnInvalidPaths { get; set; } = true;

    /// <summary>
    /// Gets or sets the Aspire CLI package version that DNX runs when the invocation is pinned to the AppHost version.
    /// </summary>
    /// <remarks>
    /// When set, and no explicit bundle or CLI path is configured, only the layout extracted for this version is
    /// accepted. Shared Aspire home layouts can belong to a different CLI and its DCP build, so they are ignored.
    /// </remarks>
    public string? PinnedAspireCliVersion { get; set; }

    /// <summary>
    /// The Aspire home directory used to find and prepare extracted bundle layouts.
    /// </summary>
    [Output]
    public string? AspireHomeDirectory { get; set; }

    /// <summary>
    /// The directory that automatic bundle setup should extract into.
    /// </summary>
    /// <remarks>
    /// This is the Aspire home directory, or a version-specific directory under it when <see cref="PinnedAspireCliVersion"/> is set.
    /// </remarks>
    [Output]
    public string? AspireCliSetupDirectory { get; set; }

    /// <summary>
    /// The resolved DCP directory.
    /// </summary>
    [Output]
    public string? DcpDir { get; set; }

    /// <summary>
    /// The resolved Dashboard directory.
    /// </summary>
    [Output]
    public string? AspireDashboardDir { get; set; }

    /// <summary>
    /// The resolved Dashboard executable path.
    /// </summary>
    [Output]
    public string? AspireDashboardPath { get; set; }

    /// <summary>
    /// The resolved TerminalHost directory.
    /// </summary>
    [Output]
    public string? AspireTerminalHostDir { get; set; }

    /// <summary>
    /// The resolved TerminalHost executable path.
    /// </summary>
    [Output]
    public string? AspireTerminalHostPath { get; set; }

    /// <summary>
    /// Invocation args that must be prepended when launching the TerminalHost binary.
    /// In the CLI bundle case the binary is the multi-mode <c>aspire-managed</c> exe and
    /// this is set to <c>"terminalhost"</c> so the dispatcher routes to <c>TerminalHostApp.RunAsync</c>.
    /// Empty for the per-RID NuGet package case (the binary is a standalone TerminalHost exe).
    /// </summary>
    [Output]
    public string? AspireTerminalHostInvocationArgs { get; set; }

    public override bool Execute()
    {
        var aspireHomeDirectory = AspireCliBundleLayoutResolver.GetAspireHomeDirectory(
            Environment.GetEnvironmentVariable(AspireCliBundleLayoutResolver.AspireHomeEnvironmentVariable));
        AspireHomeDirectory = aspireHomeDirectory;
        AspireCliSetupDirectory = aspireHomeDirectory;

        if (!string.IsNullOrWhiteSpace(AspireCliBundlePath))
        {
            if (AspireCliBundleLayoutResolver.TryResolveFromLayoutPath(AspireCliBundlePath!, out var explicitBundle))
            {
                SetOutputs(explicitBundle);
                return true;
            }

            if (WarnOnInvalidPaths)
            {
                Log.LogWarning("The AspireCliBundlePath value '{0}' does not point to a valid Aspire CLI bundle layout.", AspireCliBundlePath);
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(AspireCliPath))
        {
            if (File.Exists(AspireCliPath) && AspireCliBundleLayoutResolver.TryResolveFromCliPath(AspireCliPath!, aspireHomeDirectory, out var explicitCliBundle))
            {
                SetOutputs(explicitCliBundle);
                return true;
            }

            if (WarnOnInvalidPaths)
            {
                Log.LogWarning("The AspireCliPath value '{0}' does not point to an existing Aspire CLI executable with a valid bundle layout.", AspireCliPath);
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(PinnedAspireCliVersion))
        {
            var pinnedLayoutDirectory = AspireCliBundleLayoutResolver.GetPinnedLayoutDirectory(aspireHomeDirectory, PinnedAspireCliVersion!);
            AspireCliSetupDirectory = pinnedLayoutDirectory;

            if (AspireCliBundleLayoutResolver.TryResolveFromLayoutPath(pinnedLayoutDirectory, out var pinnedBundle))
            {
                SetOutputs(pinnedBundle);
            }

            return true;
        }

        if (SearchPathForAspireCli &&
            AspireCliBundleLayoutResolver.TryResolveFromCliPaths(CommandPathResolver.EnumerateFromPath("aspire"), aspireHomeDirectory, out var pathBundle))
        {
            SetOutputs(pathBundle);
            return true;
        }

        if (AspireCliBundleLayoutResolver.TryResolveFromLayoutPath(aspireHomeDirectory, out var aspireHomeBundle))
        {
            SetOutputs(aspireHomeBundle);
            return true;
        }

        return true;
    }

    private void SetOutputs(AspireCliBundleLayout layout)
    {
        DcpDir = EnsureTrailingDirectorySeparator(layout.DcpDir);
        AspireDashboardDir = EnsureTrailingDirectorySeparator(layout.DashboardDir);
        AspireDashboardPath = layout.DashboardPath;
        AspireTerminalHostDir = EnsureTrailingDirectorySeparator(layout.TerminalHostDir);
        AspireTerminalHostPath = layout.TerminalHostPath;
        AspireTerminalHostInvocationArgs = layout.TerminalHostInvocationArgs;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
