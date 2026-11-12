// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Azure.Tests;

public class RemoteTestOutputHelperTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void StartAndWaitTerminatesTimedOutProcess()
    {
        var options = RemoteTestOutputHelper.CreateRemoteInvokeOptions();
        options.TimeOut = 500;

        using var handle = RemoteExecutor.Invoke(
            static () => Thread.Sleep(Timeout.Infinite),
            options);

        var exception = Assert.Throws<RemoteExecutionException>(
            () => RemoteTestOutputHelper.StartAndWait(handle, testOutputHelper));

        Assert.Contains("Timed out after 500ms", exception.Message);
        Assert.True(handle.Process.HasExited);
        Assert.Contains("Timed out after 500ms", testOutputHelper.Output);
    }
}
