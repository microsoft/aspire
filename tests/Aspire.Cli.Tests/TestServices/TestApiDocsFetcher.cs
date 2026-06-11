// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test implementation of <see cref="IApiDocsFetcher"/> that returns no content.
/// </summary>
internal sealed class TestApiDocsFetcher : IApiDocsFetcher
{
    public Task<string?> FetchSitemapAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<string?> FetchPageAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}
