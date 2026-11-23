// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Tests.Dcp;
internal sealed class TestDcpDependencyCheckService(DcpInfo? dcpInfo = null) : IDcpDependencyCheckService
{
    private int _getDcpInfoCallCount;
    private int _getDcpVersionCallCount;

    public int GetDcpInfoCallCount => Volatile.Read(ref _getDcpInfoCallCount);

    public int GetDcpVersionCallCount => Volatile.Read(ref _getDcpVersionCallCount);

    public Task<DcpInfo?> GetDcpInfoAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getDcpInfoCallCount);
        return Task.FromResult(GetInfo());
    }

    public Task<DcpInfo?> GetDcpVersionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getDcpVersionCallCount);
        return Task.FromResult(GetInfo());
    }

    private DcpInfo? GetInfo()
    {
        if (dcpInfo is not null)
        {
            return dcpInfo;
        }

        return new DcpInfo
        {
            VersionString = DcpVersion.Dev.ToString(),
            Version = DcpVersion.Dev,
            Containers = new DcpContainersInfo
            {
                Runtime = "docker",
                Installed = true,
                Running = true
            }
        };
    }
}
