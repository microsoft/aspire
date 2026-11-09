// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias WatchHost;

using System.Diagnostics;
using Aspire.Hosting.Dotnet.Watch;
using WatchHost::Aspire.Hosting.Dotnet.WatchHost;
using WatchHost::Aspire.Shared.DotnetWatch;

namespace Aspire.Hosting.Dotnet.Tests.WatchHost;

// The watch host's `server` command (docs/plans/project-v2-csharpprogram-watch.md).
public class ServerHostTests
{
    [Fact]
    public async Task RejectsAMissingCommand()
    {
        Assert.Equal(ServerHost.ExitUsage, await ServerHost.RunAsync(["/usr/bin/dotnet"]));
        Assert.Equal(ServerHost.ExitUsage, await ServerHost.RunAsync(["--"]));
    }

    [Fact]
    public async Task UnknownCommandPrintsUsage()
    {
        var (exitCode, _, stderr) = await RunHostAsync(["bogus"]);

        Assert.Equal(64, exitCode);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsALaunchFailure()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-watch-host-test-");
        try
        {
            var missing = Path.Combine(directory.FullName, "missing");

            var (exitCode, _, stderr) = await RunHostAsync([DotnetWatchHostProtocol.ServerCommand, "--", missing]);

            Assert.Equal(ServerHost.ExitLaunchFailed, exitCode);
            Assert.Contains("dotnet-watch server:", stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses /bin/sh.")]
    public async Task ReturnsTheWatcherExitCodeAndOutput()
    {
        // Arguments after the executable reach it verbatim; stdin is empty and not a console. The watcher's output comes
        // through; of the guardian's records only warnings do (here, the non-zero exit).
        var (exitCode, stdout, stderr) = await RunHostAsync(
            [DotnetWatchHostProtocol.ServerCommand, "--", "/bin/sh", "-c", "echo out; echo err >&2; if [ -t 0 ]; then exit 1; fi; if read -r line; then exit 2; fi; exit 7"]);

        Assert.Equal(7, exitCode);
        Assert.Equal("out", stdout.Trim());
        var errorLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("err", errorLines[0]);
        Assert.Contains(errorLines, l => l.StartsWith("dotnet-watch server:", StringComparison.Ordinal) && l.EndsWith("exited with code 7.", StringComparison.Ordinal));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses /bin/sh.")]
    public async Task CleanExitWritesOnlyTheWatcherOutput()
    {
        // The guardian's informational records (start, clean exit) are filtered out.
        var (exitCode, stdout, stderr) = await RunHostAsync(
            [DotnetWatchHostProtocol.ServerCommand, "--", "/bin/sh", "-c", "echo out; echo err >&2"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("out", stdout.Trim());
        Assert.Equal("err", stderr.Trim());
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses POSIX signals and /bin/sh.")]
    public async Task StopSignalEndsTheWatcherTreeAfterTheGracePeriod()
    {
        // A watcher that ignores SIGTERM (as it does under DCP) with a child that would outlive it.
        var directory = Directory.CreateTempSubdirectory("aspire-watch-host-test-");
        try
        {
            var pidFile = Path.Combine(directory.FullName, "child.pid");
            using var host = StartHost(
                [DotnetWatchHostProtocol.ServerCommand, "--", "/bin/sh", "-c", $"trap '' TERM; sleep 60 & echo $! > {pidFile}; wait"],
                new() { [DotnetWatchHostProtocol.ShutdownGraceVariable] = "200" });
            try
            {
                var childId = await ReadPidAsync(pidFile);

                Process.Start("kill", ["-TERM", host.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!.WaitForExit();
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(0, host.ExitCode);
                await WaitUntilGoneAsync(childId);
            }
            finally
            {
                KillIfRunning(host);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses /bin/sh; Windows relies on the guardian's job object instead.")]
    public async Task KilledHostTakesTheWatcherTreeWithIt()
    {
        // SIGKILL can't be handled, so only the guardian (owner liveness + process group) cleans up.
        var directory = Directory.CreateTempSubdirectory("aspire-watch-host-test-");
        try
        {
            var pidFile = Path.Combine(directory.FullName, "child.pid");
            using var host = StartHost([DotnetWatchHostProtocol.ServerCommand, "--", "/bin/sh", "-c", $"sleep 60 & echo $! > {pidFile}; wait"]);
            try
            {
                var childId = await ReadPidAsync(pidFile);

                host.Kill();
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

                await WaitUntilGoneAsync(childId);
            }
            finally
            {
                KillIfRunning(host);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 1500)]
    [InlineData("250", 250)]
    [InlineData("0", 0)]
    [InlineData("-5", 1500)]
    [InlineData("soon", 1500)]
    public void ShutdownGraceDefaultsWhenInvalid(string? value, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), ServerHost.GetShutdownGrace(value ?? ""));
    }

    private static string GetHostPath()
    {
        var directory = DotnetWatchToolResolver.GetWatchHostDirectory(typeof(ServerHostTests).Assembly);
        return DotnetWatchToolResolver.ResolveWatchHostPath(directory);
    }

    private static Process StartHost(IEnumerable<string> args, Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } muxer ? muxer : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(GetHostPath());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in environment ?? [])
        {
            startInfo.Environment[name] = value;
        }

        return Process.Start(startInfo)!;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHostAsync(IEnumerable<string> args)
    {
        using var host = StartHost(args);
        try
        {
            var stdout = host.StandardOutput.ReadToEndAsync();
            var stderr = host.StandardError.ReadToEndAsync();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return (host.ExitCode, await stdout, await stderr);
        }
        finally
        {
            KillIfRunning(host);
        }
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "child did not start");
            await Task.Delay(50);
        }

        return int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilGoneAsync(int id)
    {
        // The guardian polls owner liveness once a second.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (IsRunning(id))
        {
            Assert.True(DateTime.UtcNow < deadline, $"process {id} survived");
            await Task.Delay(100);
        }
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
