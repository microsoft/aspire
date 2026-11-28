// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared;
using Aspire.Shared.Tests;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

[RequiresTools(["dotnet"])]
public class DcpConnectionLifetimeTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_WhenParentExits_StopsApiServer(bool delayHandoff)
    {
        await TestLifetimeAsync(abruptParentExit: true, delayHandoff, useSymlink: false, preventGracefulStop: false, includeControllers: false);
    }

    [Fact]
    public async Task Session_WhenControllersAreReadyAndParentExits_StopsApiServerAndControllers()
    {
        await TestLifetimeAsync(
            abruptParentExit: true,
            delayHandoff: false,
            useSymlink: false,
            preventGracefulStop: false,
            includeControllers: true);
    }

    [Fact]
    public async Task Session_WhenDisposed_StopsDcpBeforeParentExits()
    {
        // Doctor never starts controllers. Their independent shutdown must not determine
        // whether this test passes after the Doctor API server has already stopped.
        await TestLifetimeAsync(abruptParentExit: false, delayHandoff: false, useSymlink: false, preventGracefulStop: false, includeControllers: false);
    }

    [Fact]
    public async Task Session_WhenGracefulStopCannotBeRequested_KillsApiServerAndControllersBeforeParentExits()
    {
        await TestLifetimeAsync(abruptParentExit: false, delayHandoff: false, useSymlink: false, preventGracefulStop: true, includeControllers: true);
    }

    [Fact]
    public async Task Session_WhenBundleDirectoryIsSymlinked_RecognizesControllers()
    {
        await TestLifetimeAsync(abruptParentExit: true, delayHandoff: false, useSymlink: true, preventGracefulStop: false, includeControllers: true);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenProbeCompletes_StopsDcpBeforeCallerExits()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var storage = new PrivateTemporaryStorage(Path.Combine(workspace.Path, ".aspire"), new ConfigurationBuilder().Build());
        var factory = new ProcessExecutionFactory(new HostEnvironment(), storage, NullLogger<ProcessExecutionFactory>.Instance);
        ProcessTestIdentity? identity = null;
        string? sessionDirectory = null;
        var recordingFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, directory, options) =>
            {
                var execution = factory.CreateExecution(fileName, arguments, environment, directory, options);
                return new DcpConnectionTestProcess(execution, (process, _) =>
                {
                    identity = ProcessTestIdentity.Capture(process.ProcessId);
                    sessionDirectory = Assert.IsType<string>(process.EnvironmentVariables["DCP_SESSION_FOLDER"]);
                    return Task.CompletedTask;
                });
            }
        };
        var checker = new DcpConnectionChecker(
            CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), storage, nssDbOverride: null),
            recordingFactory,
            DcpConnectionTestProcess.CreateExecutionContext(workspace.Path),
            new HostEnvironment(),
            storage,
            NullLogger<DcpConnectionChecker>.Instance);

        try
        {
            var result = await checker.TestConnectionAsync(
                GetDcpDirectory(), useDeveloperCertificate: false, TestContext.Current.CancellationToken);

            Assert.True(result.Status == EnvironmentCheckStatus.Pass, $"{result.Message}: {result.Details}");
            Assert.NotNull(identity);
            Assert.False(ProcessStartTimeHelper.IsProcessRunning(identity.Value.ProcessId, identity.Value.StartTime));
            Assert.NotNull(sessionDirectory);
            Assert.Equal(Path.TrimEndingDirectorySeparator(storage.GetTempPath()), Path.GetDirectoryName(sessionDirectory));
            Assert.StartsWith("dcp-", Path.GetFileName(sessionDirectory), StringComparison.Ordinal);
            Assert.False(Directory.Exists(sessionDirectory));
        }
        finally
        {
            if (identity is { } processIdentity)
            {
                ProcessTestFixture.KillIfRunning(processIdentity);
            }
            if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
            {
                Assert.StartsWith("dcp-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
                Directory.Delete(sessionDirectory, recursive: true);
            }
        }
    }

    private static string GetDcpDirectory()
    {
        var directory = typeof(DcpConnectionLifetimeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DcpTestDirectory").Value;
        Assert.NotNull(directory);
        Assert.True(File.Exists(BundleDiscovery.GetDcpExecutablePath(directory)), $"DCP test package not found at {directory}.");

        return directory;
    }

    private async Task TestLifetimeAsync(
        bool abruptParentExit,
        bool delayHandoff,
        bool useSymlink,
        bool preventGracefulStop,
        bool includeControllers)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var readiness = new ProcessTestReadiness();
        var dcpDirectory = GetDcpDirectory();
        if (useSymlink)
        {
            var linkedDirectory = Path.Combine(workspace.Path, "linked-dcp");
            TestSymlinkHelper.TryCreateSymlink(linkedDirectory, dcpDirectory);
            dcpDirectory = linkedDirectory;
        }

        var options = new RemoteInvokeOptions
        {
            CheckExitCode = false,
            TimeOut = 180_000
        };
        options.StartInfo.RedirectStandardOutput = true;
        options.StartInfo.RedirectStandardError = true;
        using var helper = RemoteExecutor.Invoke(
            DcpConnectionTestProcess.RunAsync,
            dcpDirectory,
            workspace.Path,
            readiness.Name,
            includeControllers.ToString(),
            preventGracefulStop.ToString(),
            options);
        var output = helper.Process.StandardOutput.ReadToEndAsync();
        var error = helper.Process.StandardError.ReadToEndAsync();
        var identities = new List<ProcessTestIdentity>();
        var processes = new List<Process>();
        var sessionDirectoryFile = Path.Combine(workspace.Path, DcpConnectionTestProcess.SessionDirectoryFileName);
        string? sessionDirectory = null;

        try
        {
            var apiServer = await readiness.ReadSupervisorAsync();
            identities.Add(apiServer);
            processes.Add(Process.GetProcessById(apiServer.ProcessId));
            if (includeControllers)
            {
                // The helper reports the API server's PID before it waits for the kubeconfig.
                // Allow both helper phases to finish before timing out the pipe reads.
                var treeReadinessTimeout = DcpConnectionTestProcess.ControllerStartupAndReadinessTimeout
                    + TestConstants.DefaultTimeoutTimeSpan;
                var tree = await readiness.ReadTreeAsync(treeReadinessTimeout);
                Assert.Equal(apiServer, tree[0]);
                identities.Add(tree[1]);
                processes.Add(Process.GetProcessById(tree[1].ProcessId));
            }
            else
            {
                var readyApiServer = await readiness.ReadTargetAsync();
                Assert.Equal(apiServer, readyApiServer);
            }
            sessionDirectory = await File.ReadAllTextAsync(sessionDirectoryFile, TestContext.Current.CancellationToken);
            Assert.StartsWith("dcp-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
            if (delayHandoff)
            {
                // Exceed the checker's 20-second probe deadline without advancing the lifetime test
                // to its kill/dispose action. Managed timeout cleanup must not satisfy these assertions.
                await Task.Delay(TimeSpan.FromSeconds(21), TestContext.Current.CancellationToken);
            }

            Assert.False(helper.Process.HasExited);
            foreach (var identity in identities)
            {
                Assert.True(ProcessStartTimeHelper.IsProcessRunning(identity.ProcessId, identity.StartTime));
                outputHelper.WriteLine($"Ready DCP process: PID={identity.ProcessId}, start={identity.StartTime}.");
            }

            if (abruptParentExit)
            {
                // Killing the whole tree or disposing the helper here would mask a missing monitor.
                helper.Process.Kill(entireProcessTree: false);
                await helper.Process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(TestContext.Current.CancellationToken))).WaitAsync(
                    TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.False(Directory.Exists(sessionDirectory));
            }
            else
            {
                using (var control = new NamedPipeClientStream(".", $"{readiness.Name}-control", PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await control.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                    using var reader = new StreamReader(control, leaveOpen: true);
                    using (var writer = new StreamWriter(control, leaveOpen: true))
                    {
                        await writer.WriteLineAsync("continue");
                    }

                    Assert.Equal("completed", await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout());
                    await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(TestContext.Current.CancellationToken))).WaitAsync(
                        TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                    Assert.False(Directory.Exists(sessionDirectory));
                    Assert.False(helper.Process.HasExited);
                }

                await helper.Process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                Assert.Equal(RemoteExecutor.SuccessExitCode, helper.Process.ExitCode);
            }
        }
        finally
        {
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }

            if (!helper.Process.HasExited)
            {
                helper.Process.Kill(entireProcessTree: true);
            }

            await helper.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            outputHelper.WriteLine(await output.WaitAsync(TimeSpan.FromSeconds(10)));
            outputHelper.WriteLine(await error.WaitAsync(TimeSpan.FromSeconds(10)));
            foreach (var logFile in new[] { DcpConnectionTestProcess.StandardOutputFileName, DcpConnectionTestProcess.StandardErrorFileName })
            {
                var path = Path.Combine(workspace.Path, logFile);
                if (File.Exists(path))
                {
                    outputHelper.WriteLine(await DcpConnectionTestProcess.ReadLogAsync(path, CancellationToken.None));
                }
            }

            sessionDirectory ??= File.Exists(sessionDirectoryFile) ? await File.ReadAllTextAsync(sessionDirectoryFile) : null;
            outputHelper.WriteLine($"DCP session directory: {sessionDirectory}");
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (sessionDirectory is not null)
            {
                Assert.StartsWith("dcp-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
                if (Directory.Exists(sessionDirectory))
                {
                    Directory.Delete(sessionDirectory, recursive: true);
                }
            }
        }
    }
}
