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
    /// Multi-component prefix layout: the install prefix contains several sibling subdirectories
    /// (for example <c>cli/</c> for the binary and <c>bundle/</c> for additional payload), and the
    /// <c>.aspire-install.json</c> sidecar lives at the prefix root, one directory above the
    /// binary. Used by script-based installs and PR-route installs that stage multiple payload
    /// components under a shared prefix.
    /// </summary>
    PayloadInSubdirectories,

    /// <summary>
    /// Flat single-directory layout: the binary and the <c>.aspire-install.json</c> sidecar share
    /// a single directory. Used by packager-managed installs (winget, brew, dotnet-tool) where
    /// the package contents are extracted directly into the install location.
    /// </summary>
    PayloadColocated,
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
