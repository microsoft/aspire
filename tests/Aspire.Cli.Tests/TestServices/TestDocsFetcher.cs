// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Docs;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test implementation of IDocsFetcher that returns empty content.
/// </summary>
internal sealed class TestDocsFetcher : IDocsFetcher
{
    public Task<string?> FetchDocsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}
