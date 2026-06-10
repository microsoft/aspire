// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessUtilTests
{
    [Fact]
    public async Task Run_WaitsForOutputCallbacksToFinishBeforeCompleting()
    {
        using var scripts = new TestTempDirectory();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var spec = CreateSingleLineOutputProcessSpec(scripts, output =>
        {
            Assert.Equal("final-line", output);
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(200);

            Assert.False(pendingProcessResult.IsCompleted);

            releaseCallback.TrySetResult();

            var result = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Fact]
    public async Task Run_IncludesCapturedOutputInNonZeroExitException()
    {
        using var scripts = new TestTempDirectory();
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(CreateFailingProcessSpec(scripts, ["stdout-final-line", "stderr-final-line"], emitSecondLineToStderr: true));

        await using (processDisposable)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pendingProcessResult).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains("returned non-zero exit code 1", exception.Message);
            Assert.Contains("stdout-final-line", exception.Message);
            Assert.Contains("stderr-final-line", exception.Message);
        }
    }

    [Fact]
    public async Task Run_ReturnsCapturedOutputInProcessResult_WhenRetentionEnabled()
    {
        using var scripts = new TestTempDirectory();
        var spec = CreateFailingProcessSpec(
            scripts,
            ["stdout-final-line", "stderr-final-line"],
            emitSecondLineToStderr: true,
            throwOnNonZeroReturnCode: false,
            retainedOutputLineCount: ProcessSpec.DefaultRetainedOutputLineCount);

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(30));
            var normalizedOutput = processResult.ProcessOutput.Select(static line => line.TrimEnd()).ToArray();

            Assert.Equal(1, processResult.ExitCode);
            Assert.Equal(2, processResult.TotalProcessOutputLineCount);
            Assert.Equal(2, processResult.ProcessOutput.Count);
            Assert.Contains("stdout-final-line", normalizedOutput);
            Assert.Contains("stderr-final-line", normalizedOutput);
        }
    }

    [Fact]
    public async Task Run_PassesArgumentListToProcess()
    {
        using var scripts = new TestTempDirectory();
        var appPath = DotnetFileAppProcess.WriteApp(scripts, "argument-list.cs", """
            #:sdk Microsoft.NET.Sdk
            Console.WriteLine(args[0]);
            """);
        var outputLines = new List<string>();
        var spec = DotnetFileAppProcess.CreateDcpProcessSpec(appPath, ["argument-list-value"], onOutputData: outputLines.Add);

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var result = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("argument-list-value", outputLines);
        }
    }

    [Fact]
    public async Task Run_IncludesArgumentListInNonZeroExitException()
    {
        using var scripts = new TestTempDirectory();
        var appPath = DotnetFileAppProcess.WriteApp(scripts, "argument-list-failure.cs", """
            #:sdk Microsoft.NET.Sdk
            Environment.Exit(7);
            """);
        var spec = DotnetFileAppProcess.CreateDcpProcessSpec(
            appPath,
            ["failure-argument", "failure argument", "quote\"argument", ""],
            throwOnNonZeroReturnCode: true);

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pendingProcessResult).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains("returned non-zero exit code 7", exception.Message);
            Assert.Contains("failure-argument", exception.Message);
            Assert.Contains("-- failure-argument \"failure argument\" \"quote\\\"argument\" \"\"", exception.Message);
        }
    }

    [Fact]
    public async Task Run_ResolvesExecutableNameFromPathBeforeStartingProcess()
    {
        using var scripts = new TestTempDirectory();
        var appPath = DotnetFileAppProcess.WriteApp(scripts, "path-executable.cs", """
            #:sdk Microsoft.NET.Sdk
            Console.WriteLine("path-executable-output");
            """);
        var dotnetPath = DotnetFileAppProcess.ResolvedExecutablePath;
        var dotnetDirectory = Path.GetDirectoryName(dotnetPath);
        var dotnetExecutableName = Path.GetFileName(dotnetPath);
        var outputLines = new List<string>();
        var spec = new ProcessSpec(dotnetExecutableName)
        {
            ArgumentList = DotnetFileAppProcess.CreateArguments(appPath),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["PATH"] = dotnetDirectory ?? string.Empty
            },
            OnOutputData = outputLines.Add,
            ThrowOnNonZeroReturnCode = false,
            ResolveExecutablePath = true
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var result = await pendingProcessResult.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("path-executable-output", outputLines);
        }
    }

    [Fact]
    public void Run_Throws_WhenArgumentsAndArgumentListAreSpecified()
    {
        var spec = new ProcessSpec("dotnet")
        {
            Arguments = "--version",
            ArgumentList = ["--info"],
            ThrowOnNonZeroReturnCode = false
        };

        var exception = Assert.Throws<ArgumentException>(() => ProcessUtil.Run(spec));

        Assert.StartsWith("Specify either Arguments or ArgumentList, not both.", exception.Message);
        Assert.Equal("processSpec", exception.ParamName);
    }

    [Fact]
    public async Task Run_TruncatesCapturedOutputToLast50LinesInNonZeroExitException()
    {
        using var scripts = new TestTempDirectory();
        var lines = Enumerable.Range(1, 52).Select(i => $"line-{i}").ToArray();
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(CreateFailingProcessSpec(scripts, lines));

        await using (processDisposable)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pendingProcessResult).WaitAsync(TimeSpan.FromSeconds(30));
            var outputLines = exception.Message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var normalizedOutputLines = outputLines.Select(static line => line.TrimEnd()).ToArray();

            Assert.Contains("Command output truncated: showing last 50 of 52 lines.", exception.Message);
            Assert.DoesNotContain("line-1", outputLines);
            Assert.DoesNotContain("line-2", outputLines);
            Assert.Contains("line-3", normalizedOutputLines);
            Assert.Contains("line-52", normalizedOutputLines);
        }
    }

    private static ProcessSpec CreateSingleLineOutputProcessSpec(TestTempDirectory scripts, Action<string> onOutputData)
    {
        var appPath = DotnetFileAppProcess.WriteApp(scripts, "single-line.cs", """
            #:sdk Microsoft.NET.Sdk
            Console.WriteLine("final-line");
            """);

        return DotnetFileAppProcess.CreateDcpProcessSpec(appPath, onOutputData: onOutputData);
    }

    private static ProcessSpec CreateFailingProcessSpec(
        TestTempDirectory scripts,
        string[] lines,
        bool emitSecondLineToStderr = false,
        bool throwOnNonZeroReturnCode = true,
        int? retainedOutputLineCount = null)
    {
        var appPath = DotnetFileAppProcess.WriteApp(scripts, "failing.cs", $$"""
            #:sdk Microsoft.NET.Sdk
            var lines = new[] { {{string.Join(", ", lines.Select(static line => JsonSerializer.Serialize(line)))}} };
            var emitSecondLineToStderr = {{JsonSerializer.Serialize(emitSecondLineToStderr)}};

            for (var i = 0; i < lines.Length; i++)
            {
                if (emitSecondLineToStderr && i == 1)
                {
                    Console.Error.WriteLine(lines[i]);
                }
                else
                {
                    Console.WriteLine(lines[i]);
                }
            }

            Environment.Exit(1);
            """);

        return DotnetFileAppProcess.CreateDcpProcessSpec(
            appPath,
            throwOnNonZeroReturnCode: throwOnNonZeroReturnCode,
            retainedOutputLineCount: retainedOutputLineCount);
    }
}
