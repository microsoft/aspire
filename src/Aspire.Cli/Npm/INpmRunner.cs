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
    /// Formats a full npm package specifier (e.g., "@playwright/cli@0.1.1").
    /// </summary>
    public static string FormatPackageSpecifier(string packageName, string version) => $"{packageName}@{version}";

    /// <summary>
    /// Formats a full npm package specifier (e.g., "@playwright/cli@0.1.1").
    /// </summary>
    public static string FormatPackageSpecifier(string packageName, SemVersion version) => FormatPackageSpecifier(packageName, version.ToString());
}

/// <summary>
/// Interface for running npm CLI commands.
/// </summary>
internal interface INpmRunner
{
    /// <summary>
    /// Gets a value indicating whether npm is available on the system PATH.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Resolves a package version from the public npm registry.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@playwright/cli").</param>
    /// <param name="versionRange">The version range to resolve (e.g., "0.1").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved package info, or null if the package was not found or npm is not installed.</returns>
    Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the version currently pointed at by a package's <c>latest</c> dist-tag using
    /// the configuration npm applies to global commands.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@microsoft/aspire-cli").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version that <c>&lt;packageName&gt;@latest</c> currently resolves to.</returns>
    Task<SemVersion> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken);

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
    /// Installs a package globally from a local tarball file.
    /// </summary>
    /// <param name="tarballPath">The path to the .tgz file to install.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the installation succeeded, false otherwise.</returns>
    Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken);
}
