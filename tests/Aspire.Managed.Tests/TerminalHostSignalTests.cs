// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Managed.NuGet.Commands;
using Aspire.TerminalHost;
using Xunit;

namespace Aspire.Managed.Tests;

public partial class TerminalHostSignalTests
{
    private const int SigTerm = 15;

    [Fact]
    public async Task TerminalHostSubcommandHandlesSigTermAndUnlinksSockets()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This test sends a Unix SIGTERM directly.");

        var socketDirectory = Directory.CreateTempSubdirectory("ath-");
        try
        {
            var producerPath = Path.Combine(socketDirectory.FullName, "p.sock");
            var consumerPath = Path.Combine(socketDirectory.FullName, "h.sock");
            var controlPath = Path.Combine(socketDirectory.FullName, "c.sock");
            var socketPaths = new[] { producerPath, consumerPath, controlPath };

            // macOS sockaddr_un.sun_path has 103 usable bytes. Keep this process-level test
            // independent of the repository checkout path so it exercises signal handling there.
            Assert.All(
                socketPaths,
                path => Assert.True(
                    System.Text.Encoding.UTF8.GetByteCount(path) < 104,
                    $"Socket path is too long for macOS: {path}"));

            var startInfo = CreateTerminalHostStartInfo(producerPath, consumerPath, controlPath);

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
        finally
        {
            Directory.Delete(socketDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalHostSubcommandStopsWhenOwningAppHostIsGone()
        => await AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(hostAssemblyPath: null);

    [Fact]
    public async Task StandaloneTerminalHostStopsWhenOwningAppHostIsGone()
        => await AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(
            typeof(TerminalHostProcessRunner).Assembly.Location);

    private static async Task AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(string? hostAssemblyPath)
    {
        var socketDirectory = Directory.CreateTempSubdirectory("ath-");
        try
        {
            var parentProducerPath = Path.Combine(socketDirectory.FullName, "pp.sock");
            var parentConsumerPath = Path.Combine(socketDirectory.FullName, "ph.sock");
            var parentControlPath = Path.Combine(socketDirectory.FullName, "pc.sock");
            var parentSocketPaths = new[] { parentProducerPath, parentConsumerPath, parentControlPath };
            var producerPath = Path.Combine(socketDirectory.FullName, "p.sock");
            var consumerPath = Path.Combine(socketDirectory.FullName, "h.sock");
            var controlPath = Path.Combine(socketDirectory.FullName, "c.sock");
            var socketPaths = new[] { producerPath, consumerPath, controlPath };
            using var parentProcess = Process.Start(
                CreateTerminalHostStartInfo(parentProducerPath, parentConsumerPath, parentControlPath))
                ?? throw new InvalidOperationException("Failed to start the parent process.");
            var parentStandardOutputTask = parentProcess.StandardOutput.ReadToEndAsync();
            var parentStandardErrorTask = parentProcess.StandardError.ReadToEndAsync();

            try
            {
                var parentReadyTask = WaitForFilesAsync(parentSocketPaths, TimeSpan.FromSeconds(10));
                var parentExitedTask = parentProcess.WaitForExitAsync();
                if (await Task.WhenAny(parentReadyTask, parentExitedTask) == parentExitedTask)
                {
                    Assert.Fail(
                        $"The parent terminal host exited before binding its sockets with code {parentProcess.ExitCode}.{Environment.NewLine}" +
                        await parentStandardErrorTask);
                }

                await parentReadyTask;
                var parentIdentity = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(parentProcess.Id);
                Assert.NotNull(parentIdentity);

                var startInfo = CreateTerminalHostStartInfo(
                    producerPath,
                    consumerPath,
                    controlPath,
                    hostAssemblyPath);
                startInfo.Environment[KnownConfigNames.TerminalHostParentProcessId] =
                    parentProcess.Id.ToString(CultureInfo.InvariantCulture);
                startInfo.Environment[KnownConfigNames.TerminalHostParentProcessStartedStable] =
                    parentIdentity.Value.ToString(CultureInfo.InvariantCulture);

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start aspire-managed.");
                var standardOutputTask = process.StandardOutput.ReadToEndAsync();
                var standardErrorTask = process.StandardError.ReadToEndAsync();

                try
                {
                    var readyTask = WaitForFilesAsync(socketPaths, TimeSpan.FromSeconds(20));
                    var exitedTask = process.WaitForExitAsync();
                    if (await Task.WhenAny(readyTask, exitedTask) == exitedTask)
                    {
                        Assert.Fail(
                            $"The terminal host exited before binding its sockets with code {process.ExitCode}.{Environment.NewLine}" +
                            await standardErrorTask);
                    }

                    await readyTask;

                    parentProcess.Kill(entireProcessTree: true);
                    await parentProcess.WaitForExitAsync();
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
            finally
            {
                if (!parentProcess.HasExited)
                {
                    parentProcess.Kill(entireProcessTree: true);
                    await parentProcess.WaitForExitAsync();
                }

                await parentStandardOutputTask;
                await parentStandardErrorTask;
            }
        }
        finally
        {
            Directory.Delete(socketDirectory.FullName, recursive: true);
        }
    }

    private static ProcessStartInfo CreateTerminalHostStartInfo(
        string producerPath,
        string consumerPath,
        string controlPath,
        string? hostAssemblyPath = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (hostAssemblyPath is null)
        {
            startInfo.ArgumentList.Add(typeof(LayoutCommand).Assembly.Location);
            startInfo.ArgumentList.Add("terminalhost");
        }
        else
        {
            var testAssemblyPath = typeof(TerminalHostSignalTests).Assembly.Location;
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssemblyPath, ".runtimeconfig.json"));
            startInfo.ArgumentList.Add("--depsfile");
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssemblyPath, ".deps.json"));
            startInfo.ArgumentList.Add(hostAssemblyPath);
        }
        startInfo.ArgumentList.Add("--producer-uds");
        startInfo.ArgumentList.Add(producerPath);
        startInfo.ArgumentList.Add("--consumer-uds");
        startInfo.ArgumentList.Add(consumerPath);
        startInfo.ArgumentList.Add("--control-uds");
        startInfo.ArgumentList.Add(controlPath);
        startInfo.Environment.Remove(KnownConfigNames.TerminalHostParentProcessId);
        startInfo.Environment.Remove(KnownConfigNames.TerminalHostParentProcessStartedStable);
        return startInfo;
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
