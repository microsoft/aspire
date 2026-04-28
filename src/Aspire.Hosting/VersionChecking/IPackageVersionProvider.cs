// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Hosting.VersionChecking;

internal interface IPackageVersionProvider
{
    SemVersion? GetPackageVersion();
}
