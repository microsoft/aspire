// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Utils;

public class TemporaryWorkspaceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InitializeGitAsync_LogsProcessDetailsAndCreatesRepository()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(recordingOutput);

        await workspace.InitializeGitAsync();

        Assert.True(Directory.Exists(Path.Combine(workspace.Path, ".git")));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith($"Starting 'git init' in '{workspace.Path}'", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("'git init' started with PID ", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains(" stdout: ", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stdout closed", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stderr closed", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("finished after ", StringComparison.Ordinal) && message.EndsWith("exit code: 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunGitAsync_LargeOutput_DrainsBothStreams()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stdoutLine = new string('o', 1024);
        var stderrLine = new string('e', 1024);
        const int lineCount = 128;

        // Git's ! aliases run through its shell on every platform, including Git for Windows.
        // Each pipe receives over 128 KiB, enough to block a runner that waits before reading.
        var alias = $"!i=0; while [ \"$i\" -lt {lineCount} ]; do printf '%s\\n' '{stdoutLine}'; printf '%s\\n' '{stderrLine}' >&2; i=$((i+1)); done";

        await TemporaryWorkspace.RunGitAsync(workspace.Path, recordingOutput, ["-c", $"alias.noisy={alias}", "noisy"], TestContext.Current.CancellationToken);

        Assert.Equal(lineCount, recordingOutput.Messages.Count(message => message.EndsWith($"stdout: {stdoutLine}", StringComparison.Ordinal)));
        Assert.Equal(lineCount, recordingOutput.Messages.Count(message => message.EndsWith($"stderr: {stderrLine}", StringComparison.Ordinal)));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("exit code: 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunGitAsync_NonZeroExit_ReportsOutputAndWorkingDirectory()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string alias = "!echo test-stdout; echo test-stderr >&2; exit 7";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TemporaryWorkspace.RunGitAsync(workspace.Path, recordingOutput, ["-c", $"alias.fail={alias}", "fail"], TestContext.Current.CancellationToken));

        Assert.Equal($"'git -c alias.fail={alias} fail' in '{workspace.Path}' failed with exit code 7. stdout: test-stdout{Environment.NewLine}, stderr: test-stderr{Environment.NewLine}", exception.Message);
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stdout: test-stdout", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stderr: test-stderr", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("exit code: 7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("!echo ready; sleep 60")]
    [InlineData("!echo ready")]
    public async Task RunGitAsync_Cancellation_StopsProcessAndReportsPartialOutput(string alias)
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        recordingOutput.MessageWritten += message =>
        {
            // Cancel only after the shell is running, without relying on a timing delay.
            if (message.EndsWith("stdout: ready", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TemporaryWorkspace.RunGitAsync(workspace.Path, recordingOutput, ["-c", $"alias.wait={alias}", "wait"], cancellation.Token));

        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stdout: ready", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("was canceled", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("finished after ", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(message, @"exit code: -?\d+$"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunGitAsync_PostStartWin32Failure_Propagates(bool checkAvailability)
    {
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var expectedException = new Win32Exception(5, "Simulated post-start failure.");
        recordingOutput.MessageWritten += message =>
        {
            if (message.Contains(" stdout: ", StringComparison.Ordinal))
            {
                throw expectedException;
            }
        };

        var exception = await Assert.ThrowsAsync<Win32Exception>(() => checkAvailability
            ? GitTestHelper.EnsureGitAvailableAsync(recordingOutput)
            : GitTestHelper.RunGitAsync(workspace.Path, recordingOutput, "--version"));

        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task RunGitAsync_Timeout_StopsProcessAndReportsPartialOutput()
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var recordingOutput = new RecordingTestOutputHelper(outputHelper);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            TemporaryWorkspace.RunGitAsync(workspace.Path, recordingOutput, ["-c", "alias.wait=!echo ready; sleep 60", "wait"], TestContext.Current.CancellationToken));

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(30));
        Assert.Matches(@"^'git -c alias.wait=!echo ready; sleep 60 wait' in '.*' \(PID \d+\) timed out after 30 seconds\.$", exception.Message);
        Assert.Contains(recordingOutput.Messages, message => message.EndsWith("stdout: ready", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("timed out after ", StringComparison.Ordinal));
        Assert.Contains(recordingOutput.Messages, message => message.Contains("finished after ", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(message, @"exit code: -?\d+$"));
    }

    [Fact]
    public void Create_PreservesWorkspaceWhenFailureCaptureRequested()
    {
        const string preserveWorkspaceOnFailureKey = "PreserveWorkspaceOnFailure";
        var keyValueStorage = TestContext.Current.KeyValueStorage;
        keyValueStorage[preserveWorkspaceOnFailureKey] = true;
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var workspacePath = workspace.WorkspaceRoot.FullName;

        try
        {
            workspace.Dispose();

            Assert.True(Directory.Exists(workspacePath));

            TemporaryWorkspace.ReleasePreservation(workspacePath);

            Assert.False(Directory.Exists(workspacePath));
        }
        finally
        {
            keyValueStorage.TryRemove(preserveWorkspaceOnFailureKey, out _);
            TemporaryWorkspace.ReleasePreservation(workspacePath);
        }

        var disposableWorkspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var disposableWorkspacePath = disposableWorkspace.WorkspaceRoot.FullName;

        disposableWorkspace.Dispose();

        Assert.False(Directory.Exists(disposableWorkspacePath));
    }

    [Fact]
    public void ReleasePreservation_DeletesPreservedWorkspaceWhenRequested()
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var workspacePath = workspace.WorkspaceRoot.FullName;

        workspace.Preserve();
        workspace.Dispose();

        Assert.True(Directory.Exists(workspacePath));

        TemporaryWorkspace.ReleasePreservation(workspacePath);

        Assert.False(Directory.Exists(workspacePath));
    }

    [Fact]
    public void ReleasePreservation_LeavesPreservedWorkspaceWhenDeletionDisabled()
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var workspacePath = workspace.WorkspaceRoot.FullName;

        workspace.Preserve();
        workspace.Dispose();

        try
        {
            TemporaryWorkspace.ReleasePreservation(workspacePath, deleteDirectory: false);

            Assert.True(Directory.Exists(workspacePath));
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
    }
}
