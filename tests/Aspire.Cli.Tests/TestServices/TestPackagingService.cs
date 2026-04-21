// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestPackagingService : IPackagingService
{
    public Func<CancellationToken, Task<IEnumerable<PackageChannel>>>? GetChannelsAsyncCallback { get; init; }

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (GetChannelsAsyncCallback is not null)
        {
            return GetChannelsAsyncCallback(cancellationToken);
        }

        return Task.FromResult(Enumerable.Empty<PackageChannel>());
    }
}
