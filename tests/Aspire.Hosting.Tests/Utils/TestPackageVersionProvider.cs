// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.VersionChecking;
using Semver;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestPackageVersionProvider : IPackageVersionProvider
{
    private readonly SemVersion _version;

    public TestPackageVersionProvider(SemVersion? version = null)
    {
        _version = version ?? new SemVersion(1, 0, 0);
    }

    public SemVersion? GetPackageVersion()
    {
        return _version;
    }
}
