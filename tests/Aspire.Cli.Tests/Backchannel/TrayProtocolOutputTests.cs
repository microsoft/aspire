// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using Aspire.Cli.Backchannel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Tests.Backchannel;

public class TrayProtocolOutputTests
{
    [Fact]
    public async Task RawPipeWriterReportsClosedReader()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var output = new TrayProtocolOutput(() => TrayProtocolOutput.CreateWriter(
            new SafeFileHandle(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: false)));
        pipe.DisposeLocalCopyOfClientHandle();

        await Assert.ThrowsAsync<IOException>(() => output.WriteLineAsync(
            """{"version":1,"type":"heartbeat"}""", CancellationToken.None)).DefaultTimeout();
    }
}
