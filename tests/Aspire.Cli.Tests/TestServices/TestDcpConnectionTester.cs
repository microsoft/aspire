// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDcpConnectionTester : IDcpConnectionTester
{
    public Func<string, DcpConnectionSecurityMode, CancellationToken, Task<DcpConnectionTestResult>>? TestConnectionAsyncCallback { get; set; }

    public Task<DcpConnectionTestResult> TestConnectionAsync(string dcpDirectory, DcpConnectionSecurityMode mode, CancellationToken cancellationToken)
    {
        if (TestConnectionAsyncCallback is not null)
        {
            return TestConnectionAsyncCallback(dcpDirectory, mode, cancellationToken);
        }

        return Task.FromResult(new DcpConnectionTestResult(mode, EnvironmentCheckStatus.Pass, $"DCP {mode} connection succeeded"));
    }
}
