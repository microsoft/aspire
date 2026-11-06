// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

internal static class NuGetSearchLimits
{
    // Apply nuget.org's paging ceiling to every feed so discovery has a consistent, bounded cost.
    // https://learn.microsoft.com/nuget/api/search-query-service-resource#request-parameters
    public const int PageSize = 1000;
    public const int MaxSkip = 3000;
    public const int MaxResults = MaxSkip + PageSize;
}
