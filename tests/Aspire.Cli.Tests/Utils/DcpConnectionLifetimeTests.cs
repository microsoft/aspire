// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Hosting;
using Aspire.Shared;
using Aspire.Shared.Tests;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

[RequiresTools(["dotnet"])]
public class DcpConnectionLifetimeTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_WhenParentExits_StopsApiServerAndControllers(bool delayHandoff)
    {
        await TestLifetimeAsync(abruptParentExit: true, delayHandoff, useSymlink: false, preventGracefulStop: false);
    }

    [Fact]
    public async Task Session_WhenDisposed_StopsDcpBeforeParentExits()
    {
        await TestLifetimeAsync(abruptParentExit: false, delayHandoff: false, useSymlink: false, preventGracefulStop: false);
    }

    [Fact]
    public async Task Session_WhenGracefulStopCannotBeRequested_KillsApiServerAndControllersBeforeParentExits()
    {
        await TestLifetimeAsync(abruptParentExit: false, delayHandoff: false, useSymlink: false, preventGracefulStop: true);
    }

    [Fact]
    public async Task Session_WhenBundleDirectoryIsSymlinked_RecognizesControllers()
    {
        await TestLifetimeAsync(abruptParentExit: false, delayHandoff: false, useSymlink: true, preventGracefulStop: false);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenProbeCompletes_StopsDcpBeforeCallerExits()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var factory = new ProcessExecutionFactory(new HostEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        ProcessTestIdentity? identity = null;
        string? sessionDirectory = null;
        var recordingFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, arguments, environment, directory, options) =>
            {
                var containerRuntime = Environment.GetEnvironmentVariable(KnownConfigNames.ContainerRuntime);
                string[] testArguments = string.IsNullOrEmpty(containerRuntime)
                    ? arguments
                    : [.. arguments, "--container-runtime", containerRuntime];
                var execution = factory.CreateExecution(fileName, testArguments, environment, directory, options);
                return new DcpConnectionTestProcess(execution, (process, _) =>
                {
                    identity = ProcessTestIdentity.Capture(process.ProcessId);
                    sessionDirectory = Assert.IsType<string>(process.EnvironmentVariables["DCP_SESSION_FOLDER"]);
                    return Task.CompletedTask;
                });
            }
        };
        var checker = new DcpConnectionChecker(
            CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), nssDbOverride: null),
            recordingFactory,
            DcpConnectionTestProcess.CreateExecutionContext(workspace.Path),
            new HostEnvironment(),
            NullLogger<DcpConnectionChecker>.Instance);

        try
        {
            var result = await checker.TestConnectionAsync(
                GetDcpDirectory(), useDeveloperCertificate: false, TestContext.Current.CancellationToken);

            Assert.True(result.Status == EnvironmentCheckStatus.Pass, $"{result.Message}: {result.Details}");
            Assert.NotNull(identity);
            Assert.False(ProcessStartTimeHelper.IsProcessRunning(identity.Value.ProcessId, identity.Value.StartTime));
            Assert.NotNull(sessionDirectory);
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
                Assert.StartsWith("aspire-dcp-doctor-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
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

    private async Task TestLifetimeAsync(bool abruptParentExit, bool delayHandoff, bool useSymlink, bool preventGracefulStop)
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
            Environment.GetEnvironmentVariable(KnownConfigNames.ContainerRuntime) ?? string.Empty,
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
            // Capture the root before waiting for controller readiness so startup failures can
            // still clean up the exact owned API server.
            var apiServer = await readiness.ReadSupervisorAsync();
            identities.Add(apiServer);
            processes.Add(Process.GetProcessById(apiServer.ProcessId));
            // Account for DcpConnectionTestProcess startup and readiness times, plus a buffer for slower CI machines.
            var treeReadinessTimeout = TimeSpan.FromSeconds(2 * DcpConnectionTestProcess.StartupAndReadinessTimeoutSeconds)
                + TestConstants.DefaultTimeoutTimeSpan;
            var tree = await readiness.ReadTreeAsync(treeReadinessTimeout);
            Assert.Equal(apiServer, tree[0]);
            identities.Add(tree[1]);
            processes.Add(Process.GetProcessById(tree[1].ProcessId));
            sessionDirectory = await File.ReadAllTextAsync(sessionDirectoryFile, TestContext.Current.CancellationToken);
            Assert.StartsWith("aspire-dcp-doctor-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
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
                using var control = new NamedPipeClientStream(".", $"{readiness.Name}-control", PipeDirection.InOut, PipeOptions.Asynchronous);
                await control.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                using var reader = new StreamReader(control, leaveOpen: true);
                using var writer = new StreamWriter(control, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("continue");
                Assert.Equal("completed", await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout());
                Assert.False(helper.Process.HasExited);
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(TestContext.Current.CancellationToken))).WaitAsync(
                    TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.False(Directory.Exists(sessionDirectory));
                await writer.WriteLineAsync("exit");
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
                Assert.StartsWith("aspire-dcp-doctor-", new DirectoryInfo(sessionDirectory).Name, StringComparison.Ordinal);
                if (Directory.Exists(sessionDirectory))
                {
                    Directory.Delete(sessionDirectory, recursive: true);
                }
            }
        }
    }
}
