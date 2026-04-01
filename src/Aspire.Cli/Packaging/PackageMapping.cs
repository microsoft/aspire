// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

internal class PackageMapping(string PackageFilter, string source, string? key = null)
{
    public const string AllPackages = "*";
    public string PackageFilter { get; } = PackageFilter;
    public string Source { get; } = source;

    /// <summary>
    /// Optional friendly key name for this source in NuGet.config (e.g., "aspire-hive-pr-15643").
    /// When set, used as the &lt;add key="..." /&gt; attribute instead of the source URL/path.
    /// </summary>
    public string? Key { get; } = key;
}