// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeNuGetPackageCache : INuGetPackageCache
{
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetTemplatePackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetIntegrationPackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetCliPackagesAsyncCallback { get; set; }
    public Func<DirectoryInfo, string, bool, FileInfo?, bool, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetPackageVersionsAsyncCallback { get; set; }

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetTemplatePackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetIntegrationPackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetCliPackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Func<DirectoryInfo, string, Func<string, bool>?, bool, FileInfo?, bool, CancellationToken, Task<IEnumerable<NuGetPackage>>>? GetPackagesAsyncCallback { get; set; }

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
    {
        if (GetPackagesAsyncCallback is not null)
        {
            return GetPackagesAsyncCallback.Invoke(workingDirectory, packageId, filter, prerelease, nugetConfigFile, useCache, cancellationToken);
        }

        // Polyglot integration discovery resolves the compatible allow-list via a `tags:polyglot` search
        // (see PackageChannel.GetPolyglotCompatiblePackageIdsAsync). Tests that exercise channel discovery
        // for a non-C# AppHost but predate polyglot filtering only configure GetIntegrationPackagesAsyncCallback,
        // and they assume every package they return is discoverable. Default the tag search to echo those
        // integration packages so the allow-list does not silently strip them. Tests that need a specific
        // compatible subset set GetPackagesAsyncCallback explicitly to override this default.
        if (packageId.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
        {
            return GetIntegrationPackagesAsync(workingDirectory, prerelease, nugetConfigFile, cancellationToken);
        }

        return Task.FromResult<IEnumerable<NuGetPackage>>([]);
    }

    public Task<IEnumerable<NuGetPackage>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        => GetPackageVersionsAsyncCallback?.Invoke(workingDirectory, exactPackageId, prerelease, nugetConfigFile, useCache, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
