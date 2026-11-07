// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting;
using Aspire.Shared;
using Aspire.Shared.Tests;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Utils;

[RequiresTools(["dotnet"])]
public class DcpConnectionLifetimeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TestConnectionAsync_WhenParentExits_StopsApiServerAndControllers()
    {
        await TestLifetimeAsync(abruptParentExit: true);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenProbeCompletes_StopsDcpBeforeParentExits()
    {
        await TestLifetimeAsync(abruptParentExit: false);
    }

    private async Task TestLifetimeAsync(bool abruptParentExit)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var readiness = new ProcessTestReadiness();
        var dcpDirectory = typeof(DcpConnectionLifetimeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DcpTestDirectory").Value;
        Assert.NotNull(dcpDirectory);
        Assert.True(File.Exists(BundleDiscovery.GetDcpExecutablePath(dcpDirectory)), $"DCP test package not found at {dcpDirectory}.");

        var options = new RemoteInvokeOptions
        {
            CheckExitCode = false,
            TimeOut = 90_000
        };
        options.StartInfo.RedirectStandardOutput = true;
        options.StartInfo.RedirectStandardError = true;
        using var helper = RemoteExecutor.Invoke(
            DcpConnectionTestProcess.RunAsync,
            dcpDirectory,
            workspace.Path,
            readiness.Name,
            Environment.GetEnvironmentVariable(KnownConfigNames.ContainerRuntime) ?? string.Empty,
            options);
        var output = helper.Process.StandardOutput.ReadToEndAsync();
        var error = helper.Process.StandardError.ReadToEndAsync();
        var identities = new List<ProcessTestIdentity>();
        var processes = new List<Process>();

        try
        {
            // Capture the root before waiting for controller readiness so startup failures can
            // still clean up the exact owned API server.
            var apiServer = await readiness.ReadGuardianAsync();
            identities.Add(apiServer);
            processes.Add(Process.GetProcessById(apiServer.ProcessId));
            var tree = await readiness.ReadTreeAsync();
            Assert.Equal(apiServer, tree[0]);
            identities.Add(tree[1]);
            processes.Add(Process.GetProcessById(tree[1].ProcessId));
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

            var sessionDirectoryFile = Path.Combine(workspace.Path, DcpConnectionTestProcess.SessionDirectoryFileName);
            var sessionDirectory = File.Exists(sessionDirectoryFile) ? await File.ReadAllTextAsync(sessionDirectoryFile) : null;
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
