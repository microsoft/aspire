// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Semver;

namespace Aspire.Hosting.VersionChecking;

internal sealed class PackageVersionProvider : IPackageVersionProvider
{
    public SemVersion? GetPackageVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }
}
