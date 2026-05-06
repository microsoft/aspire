// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Identifies the on-disk layout used by an Aspire CLI install.
/// </summary>
internal enum InstallMode
{
    /// <summary>
    /// The CLI install layout could not be identified — no <c>.aspire-install.json</c> sidecar
    /// was found next to the binary or one directory above it.
    /// </summary>
    Unknown,

    /// <summary>
    /// Mode A layout: the binary lives in a child directory (typically <c>bin/</c>) and the
    /// <c>.aspire-install.json</c> sidecar lives one directory above the binary, at the
    /// install prefix root. Used by the script and PR-route installs.
    /// </summary>
    ModeA,

    /// <summary>
    /// Mode B layout: the binary and the <c>.aspire-install.json</c> sidecar live side-by-side
    /// in the same directory. Used by packager-managed installs (winget, brew, dotnet-tool).
    /// </summary>
    ModeB,
}

/// <summary>
/// Resolves the install layout (<see cref="InstallMode"/>) and prefix directory for an
/// Aspire CLI binary by locating the <c>.aspire-install.json</c> sidecar relative to the
/// binary path.
/// </summary>
internal interface IInstallPathResolver
{
    /// <summary>
    /// Resolves the install layout for the supplied CLI binary path.
    /// </summary>
    /// <param name="binaryPath">An absolute path to the CLI binary. Symbolic links are
    /// followed before searching for the sidecar.</param>
    /// <returns>
    /// A tuple of:
    /// <list type="bullet">
    /// <item><description><c>Mode</c> — the detected install layout.</description></item>
    /// <item><description><c>Prefix</c> — the directory containing the
    /// <c>.aspire-install.json</c> sidecar, or <see cref="string.Empty"/> when
    /// <c>Mode</c> is <see cref="InstallMode.Unknown"/>.</description></item>
    /// </list>
    /// </returns>
    (InstallMode Mode, string Prefix) Resolve(string binaryPath);
}
