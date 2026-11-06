// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Packaging;

internal sealed record PolyglotPackageSearchResult(
    IReadOnlyList<(NuGetPackage Package, PackageChannel Channel)> Packages,
    IReadOnlySet<string> PolyglotCompatibleIds,
    bool LimitReached,
    IReadOnlyList<string> FailedSources);
