// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.VersionChecking;
using Aspire.Shared;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestPackageFetcher : IPackageFetcher
{
    private readonly Task<List<NuGetPackage>> _versionTask;

    public bool FetchCalled { get; private set; }

    public TestPackageFetcher(Task<List<NuGetPackage>>? versionTask = null)
    {
        _versionTask = versionTask ?? Task.FromResult<List<NuGetPackage>>([]);
    }

    public Task<List<NuGetPackage>> TryFetchPackagesAsync(string appHostDirectory, CancellationToken cancellationToken)
    {
        FetchCalled = true;
        return _versionTask;
    }
}
