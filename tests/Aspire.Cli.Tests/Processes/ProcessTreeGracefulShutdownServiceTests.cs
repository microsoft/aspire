// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Processes;

public class ProcessTreeGracefulShutdownServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_UsesDcpStopProcessTreeArguments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        string[]? capturedArguments = null;
        DirectoryInfo? capturedWorkingDirectory = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (arguments, _, workingDirectory, _) =>
            {
                capturedArguments = arguments;
                capturedWorkingDirectory = workingDirectory;
            }
        };
        var startTime = ProcessStartTimeHelper.GetCurrentProcessStartTime();
        var signaler = CreateService(workspace, dcpDirectory.FullName, executionFactory);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(Environment.ProcessId, startTime, includeStartTime: true, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, executionFactory.AttemptCount);
        Assert.NotNull(capturedArguments);
        Assert.NotNull(capturedWorkingDirectory);
        Assert.Equal(workspace.WorkspaceRoot.FullName, capturedWorkingDirectory.FullName);
        Assert.Equal([
            "stop-process-tree",
            "--skip-descendants",
            "--pid",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--process-start-time",
            ProcessTreeGracefulShutdownService.FormatDcpProcessStartTime(startTime)
        ], capturedArguments);
    }

    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_OmitsStartTimeWhenRequested()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        string[]? capturedArguments = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (arguments, _, _, _) => capturedArguments = arguments
        };
        var signaler = CreateService(workspace, dcpDirectory.FullName, executionFactory);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(
            Environment.ProcessId,
            ProcessStartTimeHelper.GetCurrentProcessStartTime(),
            includeStartTime: false,
            CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(capturedArguments);
        Assert.DoesNotContain("--process-start-time", capturedArguments);
    }

    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_UsesLeasedBundleDcpPathWhenAvailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mutableDcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("bundle").CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(mutableDcpDirectory.FullName), string.Empty);

        var versionRoot = workspace.WorkspaceRoot.CreateSubdirectory("versions").CreateSubdirectory("active-version");
        var leasedDcpDirectory = versionRoot.CreateSubdirectory("dcp");
        var leasedDcpPath = BundleDiscovery.GetDcpExecutablePath(leasedDcpDirectory.FullName);
        File.WriteAllText(leasedDcpPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = new LayoutConfiguration { LayoutPath = versionRoot.FullName }
        };
        var signaler = CreateService(workspace, mutableDcpDirectory.FullName, executionFactory, bundleService);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(Environment.ProcessId, ProcessStartTimeHelper.GetCurrentProcessStartTime(), includeStartTime: false, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(leasedDcpPath, executionFactory.LastFileName);
    }

    [Fact]
    public void CreateAppHostProcessTarget_PrefersStableStartedAt()
    {
        var runtimeStartedAt = DateTimeOffset.FromUnixTimeSeconds(1000);
        var stableStartedAt = DateTimeOffset.FromUnixTimeSeconds(2000);

        var target = ProcessTreeGracefulShutdownService.CreateAppHostProcessTarget(new AppHostInformation
        {
            AppHostPath = "apphost.cs",
            ProcessId = 1234,
            StartedAt = runtimeStartedAt,
            StableStartedAt = stableStartedAt
        });

        Assert.Equal(1234, target.Pid);
        Assert.Equal(stableStartedAt, target.StartTime);
        Assert.False(target.UseRuntimeStartTime);
    }

    [Fact]
    public void CreateAppHostProcessTarget_UsesRuntimeStartedAtWhenStableStartedAtIsMissing()
    {
        var runtimeStartedAt = DateTimeOffset.FromUnixTimeSeconds(1000);

        var target = ProcessTreeGracefulShutdownService.CreateAppHostProcessTarget(new AppHostInformation
        {
            AppHostPath = "apphost.cs",
            ProcessId = 1234,
            StartedAt = runtimeStartedAt
        });

        Assert.Equal(1234, target.Pid);
        Assert.Equal(runtimeStartedAt, target.StartTime);
        Assert.True(target.UseRuntimeStartTime);
    }

    [Fact]
    public async Task StopAppHostAsync_PassesRuntimeStartTimeToDcpForLegacyAppHostOnWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The test uses a Unix sleep process while simulating the Windows shutdown path.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        using var appHostProcess = StartTerminatingShellProcess();
        try
        {
            var runtimeStartedAt = GetRuntimeProcessStartTime(appHostProcess);
            string[]? capturedArguments = null;
            var executionFactory = new TestProcessExecutionFactory
            {
                AssertionCallback = (arguments, _, _, _) =>
                {
                    capturedArguments = arguments;
                    appHostProcess.Kill(entireProcessTree: true);
                    appHostProcess.WaitForExit(5000);
                }
            };
            var signaler = CreateService(
                workspace,
                dcpDirectory.FullName,
                executionFactory,
                environment: TestEnvironment.CreateWindows());

            var result = await signaler.StopAppHostAsync(
                new AppHostInformation
                {
                    AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"),
                    ProcessId = appHostProcess.Id,
                    StartedAt = runtimeStartedAt
                },
                requestRpcStopAsync: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result);
            Assert.NotNull(capturedArguments);
            Assert.Equal([
                "stop-process-tree",
                "--skip-descendants",
                "--pid",
                appHostProcess.Id.ToString(CultureInfo.InvariantCulture),
                "--process-start-time",
                ProcessTreeGracefulShutdownService.FormatDcpProcessStartTime(runtimeStartedAt)
            ], capturedArguments);
        }
        finally
        {
            await StopProcessAsync(appHostProcess);
        }
    }

    [Fact]
    public async Task StopAppHostAsync_CleansUpCliProcessWithoutWaitingForItAsSuccessCondition()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The signal-ignoring shell process is Unix-specific.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        using var cliProcess = StartSignalIgnoringShellProcess();
        try
        {
            var signaler = CreateService(
                workspace,
                dcpDirectory.FullName,
                new TestProcessExecutionFactory(),
                timeProvider: new FakeTimeProvider());

            var result = await signaler.StopAppHostAsync(
                new AppHostInformation
                {
                    AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"),
                    ProcessId = int.MaxValue,
                    StartedAt = null,
                    CliProcessId = cliProcess.Id,
                    CliStartedAt = GetRuntimeProcessStartTime(cliProcess)
                },
                requestRpcStopAsync: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result);
            await cliProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await StopProcessAsync(cliProcess);
        }
    }

    [Fact]
    public async Task StopAppHostAsync_CleansUpCliProcessWithAdjacentRuntimeStartTime()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The signal-ignoring shell process is Unix-specific.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        using var cliProcess = StartSignalIgnoringShellProcess();
        try
        {
            var signaler = CreateService(
                workspace,
                dcpDirectory.FullName,
                new TestProcessExecutionFactory(),
                timeProvider: new FakeTimeProvider());

            var result = await signaler.StopAppHostAsync(
                new AppHostInformation
                {
                    AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"),
                    ProcessId = int.MaxValue,
                    StartedAt = null,
                    CliProcessId = cliProcess.Id,
                    CliStartedAt = GetRuntimeProcessStartTime(cliProcess).AddSeconds(1)
                },
                requestRpcStopAsync: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result);
            await cliProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await StopProcessAsync(cliProcess);
        }
    }

    [Fact]
    public async Task StopAppHostAsync_StopsLegacyAppHostWithAdjacentRuntimeStartTime()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The SIGTERM-based AppHost stop path is Unix-specific.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        using var appHostProcess = StartTerminatingShellProcess();
        try
        {
            var signaler = CreateService(
                workspace,
                dcpDirectory.FullName,
                new TestProcessExecutionFactory());

            var result = await signaler.StopAppHostAsync(
                new AppHostInformation
                {
                    AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"),
                    ProcessId = appHostProcess.Id,
                    // Released AppHosts report StartedAt from Process.StartTime and do not send
                    // StableStartedAt. The stop path must still signal the AppHost when the runtime
                    // value lands on an adjacent Unix second.
                    StartedAt = GetRuntimeProcessStartTime(appHostProcess).AddSeconds(1),
                },
                requestRpcStopAsync: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result);
            await appHostProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await StopProcessAsync(appHostProcess);
        }
    }

    private static ProcessTreeGracefulShutdownService CreateService(
        TemporaryWorkspace workspace,
        string dcpDirectory,
        TestProcessExecutionFactory executionFactory,
        IBundleService? bundleService = null,
        TimeProvider? timeProvider = null,
        IEnvironment? environment = null)
    {
        var executionContext = new CliExecutionContext(
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot.CreateSubdirectory("hives"),
            workspace.WorkspaceRoot.CreateSubdirectory("cache"),
            workspace.WorkspaceRoot.CreateSubdirectory("sdks"),
            workspace.WorkspaceRoot.CreateSubdirectory("logs"),
            Path.Combine(workspace.WorkspaceRoot.FullName, "test.log"),
            identityChannel: "local");

        return new ProcessTreeGracefulShutdownService(
            new FixedLayoutDiscovery(dcpDirectory),
            bundleService ?? new NullBundleService(),
            new LayoutProcessRunner(executionFactory),
            executionContext, environment ?? new TestEnvironment(),
            NullLogger<ProcessTreeGracefulShutdownService>.Instance,
            timeProvider ?? TimeProvider.System);
    }

    private static Process StartSignalIgnoringShellProcess()
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("trap '' TERM; exec sleep 60");

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process;
    }

    private static Process StartTerminatingShellProcess()
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec sleep 60");

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process;
    }

    private static DateTimeOffset GetRuntimeProcessStartTime(Process process)
    {
        var startTime = ProcessStartTimeHelper.TryGetRuntimeProcessStartTimeUnixSeconds(process.Id);
        Assert.NotNull(startTime);
        return DateTimeOffset.FromUnixTimeSeconds(startTime.Value);
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill/WaitForExitAsync.
        }
    }

    private sealed class FixedLayoutDiscovery(string dcpDirectory) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null)
        {
            return component == LayoutComponent.Dcp ? dcpDirectory : null;
        }

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
