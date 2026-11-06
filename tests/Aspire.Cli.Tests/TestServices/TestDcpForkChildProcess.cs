// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Hosting.Dcp;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDcpForkChildProcess : IDcpForkChildProcess
{
    public int ProcessId { get; init; } = 456;
    public int ParentProcessId => ReadParentProcessId();
    public bool HasExited => ReadHasExited();
    public int ExitCode { get; set; }
    public Func<int> ReadParentProcessId { get; set; } = () => 123;
    public Func<bool> ReadHasExited { get; set; } = () => false;
    public Func<DcpProcessIdentity> ReadIdentity { get; set; } =
        () => new(456, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
    public int IdentityReads { get; private set; }
    public int DisposeCount { get; private set; }

    public DcpProcessIdentity GetIdentity()
    {
        IdentityReads++;
        return ReadIdentity();
    }

    public void Dispose() => DisposeCount++;
}
