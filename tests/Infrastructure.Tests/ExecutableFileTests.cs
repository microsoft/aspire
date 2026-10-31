// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ExecutableFileTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "ETXTBSY is a Unix exec failure.")]
    public async Task WrittenExecutableCanBeExecutedImmediatelyWhileOtherThreadsStartProcesses()
    {
        const int Iterations = 1000;
        const int Spawners = 4;

        // Other threads keep forking, like parallel test classes in this host do. Any fork that happens while
        // this process holds a stub open for writing makes exec of that stub fail with ETXTBSY.
        using var stop = new CancellationTokenSource();
        var spawners = Enumerable.Range(0, Spawners).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]) { UseShellExecute = false })!;
                await process.WaitForExitAsync();
            }
        })).ToArray();

        var failures = new List<string>();
        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                var path = Path.Combine(_workspace.Path, $"stub-{i}");
                await ExecutableFile.WriteAsync(path, "#!/bin/sh\nexit 7\n");

                try
                {
                    using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 7)
                    {
                        failures.Add($"stub-{i}: exit code {process.ExitCode}");
                    }
                }
                catch (Win32Exception ex)
                {
                    failures.Add($"stub-{i}: {ex.Message}");
                }
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(spawners);
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{Iterations} launches failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(5))}");
    }

    [Fact]
    public async Task WritesContentsAndExecutableMode()
    {
        var path = Path.Combine(_workspace.Path, "tool");
        var contents = "#!/usr/bin/env bash\necho 'héllo'\n";

        await ExecutableFile.WriteAsync(path, contents, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        Assert.Equal(contents, await File.ReadAllTextAsync(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(path));
        }
    }

    public void Dispose() => _workspace.Dispose();
}
