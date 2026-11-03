// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class CallbackNuGetPackageCache(
    Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>> getTemplatePackagesAsyncCallback) : INuGetPackageCache
{
    public Task<NuGetPackageOperationConfiguration> CreateChannelConfigurationAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
        => Task.FromResult(NuGetPackageOperationConfiguration.Ambient(workingDirectory, cacheIdentity: "ambient"));

    public Task<NuGetPackageOperationConfiguration> CreateSourceRestrictedConfigurationAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings,
        CancellationToken cancellationToken)
        => NuGetTestHelper.CreateService().CreatePackageOperationConfigurationAsync(workingDirectory, mappings, restrictToSelectedSources: true, cancellationToken);

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
    {
        return getTemplatePackagesAsyncCallback(configuration.OriginalWorkingDirectory, prerelease, configuration.ConfigurationFile, cancellationToken);
    }

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(NuGetPackageOperationConfiguration configuration, string packageId, Func<string, bool>? filter, bool prerelease, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(NuGetPackageOperationConfiguration configuration, string exactPackageId, bool prerelease, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
