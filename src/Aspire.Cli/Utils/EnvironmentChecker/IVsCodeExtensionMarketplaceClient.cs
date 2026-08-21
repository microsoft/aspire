// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Retrieves the latest Aspire VS Code extension versions published to the Marketplace.
/// </summary>
internal interface IVsCodeExtensionMarketplaceClient
{
    Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The latest extension version published on each Marketplace release channel.
/// </summary>
internal sealed record VsCodeExtensionMarketplaceVersions(
    SemVersion? StableVersion,
    SemVersion? PreReleaseVersion);
