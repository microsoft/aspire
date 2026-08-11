// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Materializes the embedded agent telemetry hook scripts to a stable location on disk so that
/// per-client hook configuration written by <c>aspire agent init</c> can reference them by an
/// absolute path.
/// </summary>
internal interface ITelemetryHookInstaller
{
    /// <summary>
    /// Ensures the <c>track-telemetry.sh</c> and <c>track-telemetry.ps1</c> scripts exist under the
    /// stable hooks directory (<c>~/.aspire/hooks</c>) with up-to-date content, and returns their
    /// absolute paths. The shell script is written with LF line endings and the executable bit set
    /// on non-Windows platforms. Re-running refreshes the content so an upgraded CLI updates the
    /// scripts in place.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved absolute paths to the materialized scripts.</returns>
    Task<TelemetryHookScripts> EnsureInstalledAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The absolute paths to the materialized agent telemetry hook scripts.
/// </summary>
/// <param name="ShellScriptPath">Absolute path to the materialized <c>track-telemetry.sh</c>.</param>
/// <param name="PowerShellScriptPath">Absolute path to the materialized <c>track-telemetry.ps1</c>.</param>
internal sealed record TelemetryHookScripts(string ShellScriptPath, string PowerShellScriptPath);
