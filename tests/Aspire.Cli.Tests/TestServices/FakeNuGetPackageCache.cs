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

    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetTemplatePackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetIntegrationPackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => GetCliPackagesAsyncCallback?.Invoke(workingDirectory, prerelease, nugetConfigFile, cancellationToken)
           ?? Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
