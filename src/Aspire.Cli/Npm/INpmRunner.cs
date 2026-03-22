// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Npm;

/// <summary>
/// Represents the result of resolving an npm package version.
/// </summary>
internal sealed class NpmPackageInfo
{
    /// <summary>
    /// Gets the resolved version of the package.
    /// </summary>
    public required SemVersion Version { get; init; }

    /// <summary>
    /// Gets the SRI integrity hash (e.g., "sha512-...") for the package tarball.
    /// </summary>
    public required string Integrity { get; init; }
}

/// <summary>
/// Interface for running npm CLI commands.
/// </summary>
internal interface INpmRunner
{
    /// <summary>
    /// Resolves a package version and integrity hash from the npm registry.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@playwright/cli").</param>
    /// <param name="versionRange">The version range to resolve (e.g., "0.1").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved package info, or null if the package was not found or npm is not installed.</returns>
    Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a package tarball to a temporary directory using npm pack.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@playwright/cli").</param>
    /// <param name="version">The exact version to download.</param>
    /// <param name="outputDirectory">The directory to download the tarball into.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The full path to the downloaded .tgz file, or null if the operation failed.</returns>
    Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies Sigstore attestation signatures for a package by installing it into a temporary
    /// project and running npm audit signatures. This is necessary because npm audit signatures
    /// requires a project context (node_modules + package-lock.json) that doesn't exist for
    /// global tool installations.
    /// </summary>
    /// <param name="packageName">The npm package name to verify (e.g., "@playwright/cli").</param>
    /// <param name="version">The exact version to verify.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the audit passed, false otherwise.</returns>
    Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken);

    /// <summary>
    /// Installs a package globally from a local tarball file.
    /// </summary>
    /// <param name="tarballPath">The path to the .tgz file to install.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the installation succeeded, false otherwise.</returns>
    Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken);
}
