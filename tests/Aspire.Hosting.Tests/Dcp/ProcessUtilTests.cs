// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessUtilTests
{
    [Fact]
    public async Task Run_WaitsForOutputCallbacksToFinishBeforeCompleting()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spec = CreateSingleLineOutputProcessSpec(output =>
        {
            Assert.Equal("final-line", output);
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);

            Assert.False(pendingProcessResult.IsCompleted);

            releaseCallback.TrySetResult();

            var result = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
        }
    }

    private static ProcessSpec CreateSingleLineOutputProcessSpec(Action<string> onOutputData)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessSpec("cmd")
            {
                Arguments = "/c echo final-line",
                OnOutputData = onOutputData,
                ThrowOnNonZeroReturnCode = false
            };
        }

        return new ProcessSpec("sh")
        {
            Arguments = "-c \"echo final-line\"",
            OnOutputData = onOutputData,
            ThrowOnNonZeroReturnCode = false
        };
    }
}
