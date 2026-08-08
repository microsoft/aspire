// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;
using Semver;

namespace Aspire.Cli.Tests.Utils;

internal sealed class TestVsCodeExtensionMarketplaceClient : IVsCodeExtensionMarketplaceClient
{
    public Func<CancellationToken, Task<VsCodeExtensionMarketplaceVersions>>? GetLatestVersionsAsyncCallback { get; init; }

    /// <summary>
    /// Convenience shim for tests that only care about the stable channel; the returned response
    /// reports no pre-release version.
    /// </summary>
    public Func<CancellationToken, Task<SemVersion>>? StableVersionCallback { get; init; }

    public int CallCount { get; private set; }

    public async Task<VsCodeExtensionMarketplaceVersions> GetLatestVersionsAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        if (GetLatestVersionsAsyncCallback is not null)
        {
            return await GetLatestVersionsAsyncCallback(cancellationToken);
        }

        if (StableVersionCallback is not null)
        {
            var stableVersion = await StableVersionCallback(cancellationToken);
            return new VsCodeExtensionMarketplaceVersions(stableVersion, PreReleaseVersion: null);
        }

        throw new InvalidOperationException("No Marketplace callback was configured.");
    }
}
