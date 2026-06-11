// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

internal class PackageMapping(string PackageFilter, string source)
{
    public const string AllPackages = "*";
    public string PackageFilter { get; } = PackageFilter;
    public string Source { get; } = source;
}
