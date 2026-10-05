// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class CallbackNuGetPackageCache(
    Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>> getTemplatePackagesAsyncCallback) : INuGetPackageCache
{
    public Task<NuGetPackageSearchConfiguration> CreateAmbientOverlayAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
        => Task.FromResult(NuGetPackageSearchConfiguration.Ambient(workingDirectory, cacheIdentity: "ambient"));

    public async Task<NuGetPackageSearchConfiguration> CreateStandaloneAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings)
    {
        var temporaryConfig = await TemporaryNuGetConfig.CreateAsync(mappings);
        return NuGetPackageSearchConfiguration.Standalone(workingDirectory, temporaryConfig);
    }

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
    {
        return getTemplatePackagesAsyncCallback(configuration.OriginalWorkingDirectory, prerelease, configuration.ConfigurationFile, cancellationToken);
    }

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(NuGetPackageSearchConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(NuGetPackageSearchConfiguration configuration, string packageId, Func<string, bool>? filter, bool prerelease, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(NuGetPackageSearchConfiguration configuration, string exactPackageId, bool prerelease, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
