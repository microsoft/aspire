// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

internal sealed record PackageSearchResult<T>(
    IReadOnlyList<T> Packages,
    bool LimitReached,
    IReadOnlyList<string> FailedSources);
