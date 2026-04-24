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

    [Fact]
    public async Task Run_IncludesCapturedOutputInNonZeroExitException()
    {
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(CreateFailingProcessSpec(["stdout-final-line", "stderr-final-line"], emitSecondLineToStderr: true));

        await using (processDisposable)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pendingProcessResult).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("returned non-zero exit code 1", exception.Message);
            Assert.Contains("stdout-final-line", exception.Message);
            Assert.Contains("stderr-final-line", exception.Message);
        }
    }

    [Fact]
    public async Task Run_ReturnsCapturedOutputInProcessResult_WhenRetentionEnabled()
    {
        var spec = CreateFailingProcessSpec(
            ["stdout-final-line", "stderr-final-line"],
            emitSecondLineToStderr: true,
            throwOnNonZeroReturnCode: false,
            retainedOutputLineCount: ProcessSpec.DefaultRetainedOutputLineCount);

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(10));
            var normalizedOutput = processResult.ProcessOutput.Select(static line => line.TrimEnd()).ToArray();

            Assert.Equal(1, processResult.ExitCode);
            Assert.Equal(2, processResult.TotalProcessOutputLineCount);
            Assert.Equal(2, processResult.ProcessOutput.Count);
            Assert.Contains("stdout-final-line", normalizedOutput);
            Assert.Contains("stderr-final-line", normalizedOutput);
        }
    }

    [Fact]
    public async Task Run_TruncatesCapturedOutputToLast50LinesInNonZeroExitException()
    {
        var lines = Enumerable.Range(1, 52).Select(i => $"line-{i}").ToArray();
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(CreateFailingProcessSpec(lines));

        await using (processDisposable)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pendingProcessResult).WaitAsync(TimeSpan.FromSeconds(10));
            var outputLines = exception.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var normalizedOutputLines = outputLines.Select(static line => line.TrimEnd()).ToArray();

            Assert.Contains("Command output truncated: showing last 50 of 52 lines.", exception.Message);
            Assert.DoesNotContain("line-1", outputLines);
            Assert.DoesNotContain("line-2", outputLines);
            Assert.Contains("line-3", normalizedOutputLines);
            Assert.Contains("line-52", normalizedOutputLines);
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

    private static ProcessSpec CreateFailingProcessSpec(
        string[] lines,
        bool emitSecondLineToStderr = false,
        bool throwOnNonZeroReturnCode = true,
        int? retainedOutputLineCount = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var commandParts = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (emitSecondLineToStderr && i == 1)
                {
                    commandParts.Add($"echo {lines[i]} 1>&2");
                }
                else
                {
                    commandParts.Add($"echo {lines[i]}");
                }
            }

            commandParts.Add("exit /b 1");

            return new ProcessSpec("cmd")
            {
                Arguments = $"/c \"{string.Join(" & ", commandParts)}\"",
                ThrowOnNonZeroReturnCode = throwOnNonZeroReturnCode,
                RetainedOutputLineCount = retainedOutputLineCount
            };
        }

        var unixCommandParts = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (emitSecondLineToStderr && i == 1)
            {
                unixCommandParts.Add($"echo {lines[i]} 1>&2");
            }
            else
            {
                unixCommandParts.Add($"echo {lines[i]}");
            }
        }

        unixCommandParts.Add("exit 1");

        return new ProcessSpec("sh")
        {
            Arguments = $"-c \"{string.Join("; ", unixCommandParts)}\"",
            ThrowOnNonZeroReturnCode = throwOnNonZeroReturnCode,
            RetainedOutputLineCount = retainedOutputLineCount
        };
    }
}
