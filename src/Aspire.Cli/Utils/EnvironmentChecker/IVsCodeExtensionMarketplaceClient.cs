// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Retrieves the latest published Aspire VS Code extension versions from the Marketplace.
/// </summary>
internal interface IVsCodeExtensionMarketplaceClient
{
    /// <summary>
    /// Gets the latest stable and pre-release extension versions.
    /// </summary>
    Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The latest extension version published on each Marketplace release channel. A channel is
/// <see langword="null" /> when the Marketplace has never published a version on it.
/// </summary>
internal sealed record VsCodeExtensionMarketplaceVersions(
    SemVersion? StableVersion,
    SemVersion? PreReleaseVersion);
