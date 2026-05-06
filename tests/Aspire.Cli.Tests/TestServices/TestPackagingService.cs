// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestPackagingService : IPackagingService
{
    public Func<CancellationToken, Task<IEnumerable<PackageChannel>>>? GetChannelsAsyncCallback { get; set; }

    public Func<string?>? GetStagingChannelUnavailableReasonCallback { get; set; }

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (GetChannelsAsyncCallback is not null)
        {
            return GetChannelsAsyncCallback(cancellationToken);
        }

        // Default: Return a fake channel with template packages
        var testChannel = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());
        return Task.FromResult<IEnumerable<PackageChannel>>(new[] { testChannel });
    }

    public string? GetStagingChannelUnavailableReason()
    {
        return GetStagingChannelUnavailableReasonCallback?.Invoke();
    }
}
