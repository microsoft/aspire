// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class CallbackNuGetPackageCache(
    Func<DirectoryInfo, bool, FileInfo?, CancellationToken, Task<IEnumerable<NuGetPackage>>> getTemplatePackagesAsyncCallback) : INuGetPackageCache
{
    public Task<IEnumerable<NuGetPackage>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
    {
        return getTemplatePackagesAsyncCallback(workingDirectory, prerelease, nugetConfigFile, cancellationToken);
    }

    public Task<IEnumerable<NuGetPackage>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);

    public Task<IEnumerable<NuGetPackage>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<NuGetPackage>>([]);
}
