// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

public class ProcessExecutionDetachedTests(ITestOutputHelper outputHelper)
{
    // Regression test for the duplicate-handle bug that broke `aspire start` on Windows:
    // The Windows detached path points both Stdout and Stderr at the same NUL
    // handle, and PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects duplicate handle values —
    // CreateProcessW returns ERROR_INVALID_PARAMETER (87). The unified
    // WindowsProcessInterop.SpawnProcess de-duplicates the inheritable
    // handle list, so this spawn must succeed.
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StartAsync_OnWindows_WithSharedStdoutStderrHandle_Succeeds()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        // A short-lived child is sufficient: we only need CreateProcessW to return successfully.
        // `cmd.exe /c exit 0` returns immediately and never touches stdout/stderr, so any
        // failure mode here is from the spawn primitive, not from the child itself.
        await using var child = CreateDetachedExecution(
            "cmd.exe",
            ["/c", "exit", "0"],
            Environment.CurrentDirectory);

        Assert.True(await child.StartAsync(CancellationToken.None));
        Assert.True(child.ProcessId > 0);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task StartAsync_OnUnix_InvokesDcpForkProcessWithExpectedContext()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, "fake-dcp");
        var capturePath = Path.Combine(workspace.WorkspaceRoot.FullName, "capture.txt");
        await File.WriteAllTextAsync(dcpPath, """
#!/bin/sh
if [ "$1" != "fork-process" ]; then
  exit 42
fi
if [ "$2" != "--monitor" ]; then
  exit 43
fi
if [ "$4" != "--monitor-identity-time" ]; then
  exit 44
fi
if [ "$6" != "--" ]; then
  exit 45
fi
monitor_pid="$3"
monitor_started="$5"
shift 6
{
  printf 'cwd=%s\n' "$PWD"
  printf 'monitorPid=%s\n' "$monitor_pid"
  printf 'monitorStarted=%s\n' "$monitor_started"
  printf 'cmd=%s\n' "$1"
  printf 'arg1=%s\n' "$2"
  printf 'arg2=%s\n' "$3"
  printf 'home=%s\n' "${HOME:-missing}"
  printf 'added=%s\n' "$ASPIRE_TEST_ADDED"
} > "$ASPIRE_TEST_CAPTURE"
sleep 60 >/dev/null 2>&1 </dev/null &
child_pid=$!
echo $child_pid
wait $child_pid
""");
        File.SetUnixFileMode(dcpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var detachedProcess = CreateDetachedExecution(
            "/bin/sh",
            ["-c", "exit 0"],
            workspace.WorkspaceRoot.FullName,
            environmentVariableFilter: name => string.Equals(name, "HOME", StringComparison.Ordinal),
            environment: new Dictionary<string, string>
            {
                ["ASPIRE_TEST_ADDED"] = "value",
                ["ASPIRE_TEST_CAPTURE"] = capturePath
            },
            dcpPath: dcpPath);

        Assert.True(await detachedProcess.StartAsync(CancellationToken.None));

        try
        {
            Assert.True(detachedProcess.ProcessId > 0);
            Assert.Equal([
                $"cwd={PathNormalizer.ResolveSymlinks(workspace.WorkspaceRoot.FullName)}",
                $"monitorPid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}",
                $"monitorStarted={ProcessTreeGracefulShutdownService.FormatDcpProcessStartTime(IsolatedProcess.GetCurrentProcessDcpMonitorStartTime())}",
                "cmd=/bin/sh",
                "arg1=-c",
                "arg2=exit 0",
                "home=missing",
                "added=value"
            ], await File.ReadAllLinesAsync(capturePath));
        }
        finally
        {
            if (!detachedProcess.HasExited)
            {
                detachedProcess.Kill(entireProcessTree: true);
                await detachedProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task StartAsync_OnUnix_ReturnsForkedProcessWhenCancelledAfterDcpStarts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, "fake-dcp");
        var startedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "dcp-started.txt");
        await File.WriteAllTextAsync(dcpPath, """
#!/bin/sh
if [ "$1" != "fork-process" ]; then
  exit 42
fi
if [ "$2" != "--monitor" ]; then
  exit 43
fi
if [ "$6" != "--" ]; then
  exit 44
fi
printf 'started\n' > "$ASPIRE_TEST_STARTED"
sleep 1
sleep 60 >/dev/null 2>&1 </dev/null &
child_pid=$!
echo $child_pid
wait $child_pid
""");
        File.SetUnixFileMode(dcpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var cts = new CancellationTokenSource();
        IProcessExecution? detachedProcess = null;
        var detachedExecution = CreateDetachedExecution(
            "/bin/sh",
            ["-c", "exit 0"],
            workspace.WorkspaceRoot.FullName,
            environment: new Dictionary<string, string>
            {
                ["ASPIRE_TEST_STARTED"] = startedPath
            },
            dcpPath: dcpPath);
        var startTask = detachedExecution.StartAsync(cts.Token);

        try
        {
            await WaitForFileAsync(startedPath).WaitAsync(TimeSpan.FromSeconds(5));
            await cts.CancelAsync();

            Assert.True(await startTask.WaitAsync(TimeSpan.FromSeconds(5)));
            detachedProcess = detachedExecution;

            Assert.True(detachedProcess.ProcessId > 0);
        }
        finally
        {
            if (detachedProcess is null)
            {
                try
                {
                    await startTask.WaitAsync(TimeSpan.FromSeconds(5));
                    detachedProcess = detachedExecution;
                }
                catch
                {
                    // The assertion failure will report the original problem; this best-effort wait
                    // only prevents a successfully-forked child from being orphaned by the test.
                }
            }

            if (detachedProcess is { HasExited: false })
            {
                detachedProcess.Kill(entireProcessTree: true);
                await detachedProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (detachedProcess is not null)
            {
                await detachedProcess.DisposeAsync();
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task StartAsync_OnUnix_DcpForkProcessStderrDoesNotBlockSuccessfulLaunch()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, "fake-dcp");
        await File.WriteAllTextAsync(dcpPath, """
#!/bin/sh
if [ "$1" != "fork-process" ]; then
  exit 42
fi
if [ "$2" != "--monitor" ]; then
  exit 43
fi
if [ "$6" != "--" ]; then
  exit 44
fi
sleep 0.1 >/dev/null 2>&1 </dev/null &
child_pid=$!
echo $child_pid
printf 'diagnostic warning\n' >&2
wait $child_pid
""");
        File.SetUnixFileMode(dcpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var detachedProcess = CreateDetachedExecution(
            "/bin/sh",
            ["-c", "exit 0"],
            workspace.WorkspaceRoot.FullName,
            dcpPath: dcpPath);

        Assert.True(await detachedProcess.StartAsync(CancellationToken.None));
        await detachedProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task StartAsync_OnUnix_ReturnsMonitorExitCodeWhenForkedProcessAlreadyExited()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-only test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, "fake-dcp");
        var capturePath = Path.Combine(workspace.WorkspaceRoot.FullName, "child-pid.txt");
        await File.WriteAllTextAsync(dcpPath, """
#!/bin/sh
if [ "$1" != "fork-process" ]; then
  exit 42
fi
if [ "$2" != "--monitor" ]; then
  exit 43
fi
if [ "$6" != "--" ]; then
  exit 44
fi
/bin/sh -c 'exit 11' >/dev/null 2>&1 </dev/null &
child_pid=$!
wait $child_pid
exit_code=$?
printf '%s\n' "$child_pid" > "$ASPIRE_TEST_CAPTURE"
printf '%s\n' "$child_pid"
sleep 0.2
exit "$exit_code"
""");
        File.SetUnixFileMode(dcpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await using var detachedProcess = CreateDetachedExecution(
            "/bin/sh",
            ["-c", "exit 0"],
            workspace.WorkspaceRoot.FullName,
            environment: new Dictionary<string, string>
            {
                ["ASPIRE_TEST_CAPTURE"] = capturePath
            },
            dcpPath: dcpPath);

        Assert.True(await detachedProcess.StartAsync(CancellationToken.None));

        var childPid = int.Parse(await File.ReadAllTextAsync(capturePath), CultureInfo.InvariantCulture);
        Assert.Equal(childPid, detachedProcess.ProcessId);
        Assert.True(detachedProcess.HasExited);
        Assert.Equal(11, detachedProcess.ExitCode);
        await detachedProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForFileAsync(string path)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private static IProcessExecution CreateDetachedExecution(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? environmentVariableFilter = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? dcpPath = null,
        ILogger<ProcessExecutionFactory>? logger = null)
    {
        var factory = new ProcessExecutionFactory(new TestEnvironment(), logger ?? NullLogger<ProcessExecutionFactory>.Instance);
        return factory.CreateExecution(
            fileName,
            arguments.ToArray(),
            environment?.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value),
            new DirectoryInfo(workingDirectory),
            new ProcessInvocationOptions
            {
                Detached = true,
                IsolateConsole = true,
                EnvironmentVariableFilter = environmentVariableFilter,
                DetachedUnixLauncherPathOverride = dcpPath
            });
    }
}
