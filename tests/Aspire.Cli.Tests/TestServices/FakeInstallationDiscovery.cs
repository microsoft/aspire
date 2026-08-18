// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Fixed-result <see cref="IInstallationDiscovery"/> for command tests.
/// Returns a curated <c>self</c> row and (optionally) curated peers from
/// <see cref="DiscoverAllAsync"/>, decoupling the test from host filesystem
/// state, <c>$PATH</c>, and any real sidecar files.
/// </summary>
internal sealed class FakeInstallationDiscovery : IInstallationDiscovery
{
    private readonly InstallationInfo _self;
    private readonly IReadOnlyList<InstallationInfo> _others;
    private readonly Exception? _discoverAllException;

    public Func<CancellationToken, Task<IReadOnlyList<InstallationInfo>>>? DiscoverAllAsyncCallback { get; init; }

    /// <summary>
    /// When set, called instead of returning the fixed <c>self</c> row. May throw to
    /// simulate a self-probe failure (including <see cref="OperationCanceledException"/>).
    /// </summary>
    public Func<InstallationInfo>? DescribeSelfCallback { get; init; }

    public FakeInstallationDiscovery(InstallationInfo self, IReadOnlyList<InstallationInfo>? others = null, Exception? discoverAllException = null)
    {
        _self = self;
        _others = others ?? [];
        _discoverAllException = discoverAllException;
    }

    public InstallationInfo DescribeSelf() => DescribeSelfCallback is not null ? DescribeSelfCallback() : _self;

    public Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DiscoverAllAsyncCallback is not null)
        {
            return DiscoverAllAsyncCallback(cancellationToken);
        }

        if (_discoverAllException is not null)
        {
            throw _discoverAllException;
        }

        // Self is always the first element by InstallationDiscovery contract.
        IReadOnlyList<InstallationInfo> all = [_self, .. _others];
        return Task.FromResult(all);
    }
}
