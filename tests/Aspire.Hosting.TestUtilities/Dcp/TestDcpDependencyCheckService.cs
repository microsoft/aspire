// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Tests.Dcp;
internal sealed class TestDcpDependencyCheckService : IDcpDependencyCheckService
{
    public Task<DcpInfo?> GetDcpInfoAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var dcpInfo = new DcpInfo
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
        return Task.FromResult((DcpInfo?)dcpInfo);
    }
}
