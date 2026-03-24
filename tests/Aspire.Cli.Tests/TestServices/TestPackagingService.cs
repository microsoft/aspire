// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestPackagingService : IPackagingService
{
    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Enumerable.Empty<PackageChannel>());
    }
}
