// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeNuGetPackageCache : INuGetPackageCache
{
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetTemplatePackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetIntegrationPackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetCliPackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, string, bool, FileInfo?, bool, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetPackageVersionsAsyncCallback { get; set; }

    public Task<NuGetPackageOperationConfiguration> CreateChannelConfigurationAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<PackageMapping>? channelMappings,
        CancellationToken cancellationToken)
    {
        return NuGetTestHelper.CreateService().CreatePackageOperationConfigurationAsync(
            workingDirectory, channelMappings, restrictToSelectedSources: false, cancellationToken);
    }

    public Task<NuGetPackageOperationConfiguration> CreateSourceRestrictedConfigurationAsync(
        DirectoryInfo workingDirectory,
        PackageMapping[] mappings,
        CancellationToken cancellationToken)
        => NuGetTestHelper.CreateService().CreatePackageOperationConfigurationAsync(workingDirectory, mappings, restrictToSelectedSources: true, cancellationToken);

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => GetTemplatePackagesAsyncCallback?.Invoke(configuration.OriginalWorkingDirectory, prerelease, configuration.ConfigurationFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => GetIntegrationPackagesAsyncCallback?.Invoke(configuration.OriginalWorkingDirectory, prerelease, configuration.ConfigurationFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(NuGetPackageOperationConfiguration configuration, bool prerelease, CancellationToken cancellationToken)
        => GetCliPackagesAsyncCallback?.Invoke(configuration.OriginalWorkingDirectory, prerelease, configuration.ConfigurationFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Func<DirectoryInfo, string, Func<string, bool>?, bool, FileInfo?, bool, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetPackagesAsyncCallback { get; set; }

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(NuGetPackageOperationConfiguration configuration, string packageId, Func<string, bool>? filter, bool prerelease, bool useCache, CancellationToken cancellationToken)
    {
        if (GetPackagesAsyncCallback is not null)
        {
            return GetPackagesAsyncCallback.Invoke(configuration.OriginalWorkingDirectory, packageId, filter, prerelease, configuration.ConfigurationFile, useCache, cancellationToken);
        }

        // Polyglot integration discovery resolves the compatible allow-list via a `tags:polyglot` search
        // (see PackageChannel.GetPolyglotCompatiblePackageIdsAsync). Tests that exercise channel discovery
        // for a non-C# AppHost but predate polyglot filtering only configure GetIntegrationPackagesAsyncCallback,
        // and they assume every package they return is discoverable. Default the tag search to echo those
        // integration packages so the allow-list does not silently strip them. Tests that need a specific
        // compatible subset set GetPackagesAsyncCallback explicitly to override this default.
        if (packageId.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
        {
            return GetIntegrationPackagesAsync(configuration, prerelease, cancellationToken);
        }

        return Task.FromResult<IEnumerable<NuGetPackage>>([]);
    }

    public Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(NuGetPackageOperationConfiguration configuration, string exactPackageId, bool prerelease, bool useCache, CancellationToken cancellationToken)
        => GetPackageVersionsAsyncCallback?.Invoke(configuration.OriginalWorkingDirectory, exactPackageId, prerelease, configuration.ConfigurationFile, useCache, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
