// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Managed.Tests;

public partial class TerminalHostSignalTests
{
    private const int SigTerm = 15;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalHostSubcommandUnlinksSocketsAfterTermination(bool forceTermination)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This test sends a Unix SIGTERM directly.");

        var root = Directory.CreateTempSubdirectory();
        try
        {
            var socketDirectory = new DirectoryInfo(Path.Combine(root.FullName, "terminals"));
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
                        $"stdout: {await standardOutputTask}{Environment.NewLine}stderr: {await standardErrorTask}");
                }

                await readyTask;

                if (forceTermination)
                {
                    process.Kill(entireProcessTree: false);
                }
                else
                {
                    Assert.Equal(0, SendSignal(process.Id, SigTerm));
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

                var standardOutput = await standardOutputTask;
                var standardError = await standardErrorTask;
                Assert.True(
                    forceTermination || process.ExitCode == 0,
                    $"aspire-managed exited with code {process.ExitCode}.{Environment.NewLine}" +
                    $"stdout: {standardOutput}{Environment.NewLine}stderr: {standardError}");
                // A forcibly killed shim cannot wait for its child's parent watchdog.
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (socketPaths.Any(File.Exists))
                {
                    await Task.Delay(50, cleanupCts.Token);
                }
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
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TerminalHostSubcommandStopsWhenOwningAppHostIsGone()
        => await AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(hostAssemblyPath: null);

    [Fact]
    public async Task TerminalHostControlShutdownUnlinksSockets()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new[] { "p.sock", "h.sock", "c.sock" }
                .Select(name => Path.Combine(root.FullName, "terminals", name)).ToArray();
            var startInfo = CreateTerminalHostStartInfo(paths[0], paths[1], paths[2]);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await WaitForFilesAsync(paths, TimeSpan.FromSeconds(10));
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await TerminalHostForwarder.RequestShutdownAsync(startInfo.ArgumentList.ToArray(), shutdownTimeout.Token);
                await process.WaitForExitAsync().WaitAsync(shutdownTimeout.Token);
                Assert.True(process.ExitCode == 0,
                    $"Terminal host exited with code {process.ExitCode}.{Environment.NewLine}stdout: {await stdout}{Environment.NewLine}stderr: {await stderr}");
                Assert.All(paths, path => Assert.False(File.Exists(path), $"Expected '{path}' to be unlinked."));
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
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false, 64)]
    [InlineData(true, 1)]
    public async Task TerminalHostForwarderReportsMissingPayloadAndPropagatesExitStatus(bool missingPayload, int expectedExitCode)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var startInfo = CreateTerminalHostStartInfo(Path.Combine(root.FullName, "s", "p.sock"), "consumer", "control");
            startInfo.ArgumentList.Add("--invalid");
            if (missingPayload)
            {
                File.Delete(Path.Combine(root.FullName, "terminalhost", OperatingSystem.IsWindows() ? "Aspire.TerminalHost.exe" : "Aspire.TerminalHost"));
            }

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedExitCode, process.ExitCode);
            Assert.Contains(missingPayload ? "Terminal host executable was not found" : "--invalid", await stderr);
            await stdout;
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StandaloneTerminalHostStopsWhenOwningAppHostIsGone()
        => await AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(
            "Aspire.TerminalHost.dll");

    private static async Task AssertTerminalHostStopsWhenOwningAppHostIsGoneAsync(string? hostAssemblyPath)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var socketDirectory = new DirectoryInfo(Path.Combine(root.FullName, "terminals"));
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
                        $"stdout: {await parentStandardOutputTask}{Environment.NewLine}stderr: {await parentStandardErrorTask}");
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
                            $"stdout: {await standardOutputTask}{Environment.NewLine}stderr: {await standardErrorTask}");
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
            root.Delete(recursive: true);
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
            // Reproduce the shipped sibling layout without sharing mutable output between tests.
            var root = Path.GetDirectoryName(Path.GetDirectoryName(producerPath))!;
            var managed = Directory.CreateDirectory(Path.Combine(root, "managed"));
            var terminal = Directory.CreateDirectory(Path.Combine(root, "terminalhost"));
            foreach (var (fixture, directory) in new[] { ("terminalhost", terminal.FullName), ("managed", managed.FullName) })
            {
                var output = Path.Combine(AppContext.BaseDirectory, "ProcessFixtures", fixture);
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(directory, Path.GetRelativePath(output, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target);
                    }
                }
            }
            var managedAssembly = Path.Combine(managed.FullName, "aspire-managed.dll");
            startInfo.ArgumentList.Add(managedAssembly);
            startInfo.ArgumentList.Add("terminalhost");
        }
        else
        {
            var output = Path.Combine(AppContext.BaseDirectory, "ProcessFixtures", "terminalhost");
            startInfo.ArgumentList.Add(Path.Combine(output, Path.GetFileName(hostAssemblyPath)));
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
