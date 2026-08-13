// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Aspire.Managed.Tests;

public partial class TerminalHostSignalTests(ITestOutputHelper outputHelper)
{
    private const int SigTerm = 15;

    [Fact]
    public async Task TerminalHostSubcommandHandlesSigTermAndUnlinksSockets()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This test sends a Unix SIGTERM directly.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var producerPath = Path.Combine(workspace.Path, "producer.sock");
        var consumerPath = Path.Combine(workspace.Path, "consumer.sock");
        var controlPath = Path.Combine(workspace.Path, "control.sock");
        var socketPaths = new[] { producerPath, consumerPath, controlPath };

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(ParentProcessWatchdog).Assembly.Location);
        startInfo.ArgumentList.Add("terminalhost");
        startInfo.ArgumentList.Add("--producer-uds");
        startInfo.ArgumentList.Add(producerPath);
        startInfo.ArgumentList.Add("--consumer-uds");
        startInfo.ArgumentList.Add(consumerPath);
        startInfo.ArgumentList.Add("--control-uds");
        startInfo.ArgumentList.Add(controlPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start aspire-managed.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        try
        {
            var readyTask = WaitForFilesAsync(socketPaths, TimeSpan.FromSeconds(10));
            var exitedTask = process.WaitForExitAsync();
            if (await Task.WhenAny(readyTask, exitedTask) == exitedTask)
            {
                Assert.Fail(
                    $"aspire-managed exited before binding its sockets with code {process.ExitCode}.{Environment.NewLine}" +
                    await standardErrorTask);
            }

            await readyTask;

            Assert.Equal(0, SendSignal(process.Id, SigTerm));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            Assert.True(
                process.ExitCode == 0,
                $"aspire-managed exited with code {process.ExitCode}.{Environment.NewLine}" +
                $"stdout: {standardOutput}{Environment.NewLine}stderr: {standardError}");
            Assert.All(socketPaths, path => Assert.False(File.Exists(path), $"Expected '{path}' to be unlinked."));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task WaitForFilesAsync(IEnumerable<string> paths, TimeSpan timeout)
    {
        var expectedPaths = paths.ToArray();
        var deadline = DateTime.UtcNow + timeout;
        while (expectedPaths.Any(path => !File.Exists(path)))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {string.Join(", ", expectedPaths)}");
            }

            await Task.Delay(50);
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
