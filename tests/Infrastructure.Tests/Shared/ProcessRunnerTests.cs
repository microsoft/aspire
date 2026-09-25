// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ProcessRunnerTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["bash"])]
    public async Task CapturesStreamsExitCodeAndInvocationContext()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        File.WriteAllText(Path.Combine(workspace.Path, "marker.txt"), "working directory");

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """printf '%s\n' "$1" "$RUNNER_TEST_VALUE" "$(cat marker.txt)"; cat; printf 'error' >&2; exit 7""", "test", "argument with spaces"],
            workspace.Path,
            environment: new Dictionary<string, string> { ["RUNNER_TEST_VALUE"] = "environment value" },
            standardInput: "input");

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("argument with spaces\nenvironment value\nworking directory\ninput", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
        Assert.Equal(result.StandardOutput + result.StandardError, result.Output);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task DrainsOutputWhileWritingLargeInput()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var input = new string('i', 256 * 1024);

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """printf '%262144s' ''; printf '%262144s' '' >&2; cat"""],
            workspace.Path,
            standardInput: input);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string(' ', 256 * 1024) + input, result.StandardOutput);
        Assert.Equal(new string(' ', 256 * 1024), result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash"])]
    public async Task TimeoutKillsProcessTreeEvenWhenInputIsBlocked(bool sendInput)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """echo $$ > parent.pid; sleep 60 & echo $! > child.pid; wait"""],
            workspace.Path,
            standardInput: sendInput ? new string('i', 256 * 1024) : null,
            timeout: TimeSpan.FromSeconds(2)));

        Assert.Equal("Process did not exit within 2 seconds: bash", exception.Message);
        AssertProcessExited("parent.pid");
        AssertProcessExited("child.pid");

        void AssertProcessExited(string fileName)
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(workspace.Path, fileName)));
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // An already-reaped process has no entry to inspect.
                return;
            }

            using (process)
            {
                Assert.True(process.HasExited, $"Process {pid} is still running after timeout.");
            }
        }
    }
}
