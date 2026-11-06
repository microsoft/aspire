// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Watch;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal static class ProcessTestHelpers
{
    public static async Task AssertWatchDisabledWithInheritedEnvironmentAsync(
        IDictionary<string, string> environment, DirectoryInfo workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory.FullName };
        startInfo.Environment[DotnetWatchLaunchEnvironment.WatchEnabledVariable] = "true";
        string[] privateNames =
        [
            DotnetWatchConfiguration.LaunchOwner, DotnetWatchConfiguration.CliExecutable, DotnetWatchConfiguration.CliArguments,
            DotnetWatchConfiguration.ToolPath, DotnetWatchConfiguration.ToolVersion, DotnetWatchConfiguration.ToolRuntimeMajor,
            DotnetWatchConfiguration.HostControlPipe, DotnetWatchConfiguration.HostSession
        ];
        foreach (var name in privateNames)
        {
            startInfo.Environment[name] = name == DotnetWatchConfiguration.LaunchOwner ? "aspire" : "parent-generation";
        }
        startInfo.Environment["DOTNET_WATCH"] = "1";
        startInfo.Environment["DEBUG_SESSION_PORT"] = "localhost:12345";
        startInfo.Environment["UNRELATED_SETTING"] = "preserved";
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        // Seed inheritance on this child only; mutating the test process would race other launches.
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        await using var execution = factory.CreateExecution(startInfo, new ProcessInvocationOptions());

        Assert.Equal("false", execution.EnvironmentVariables[DotnetWatchLaunchEnvironment.WatchEnabledVariable]);
        foreach (var name in privateNames)
        {
            Assert.Equal("", execution.EnvironmentVariables[name]);
        }
        Assert.Equal("1", execution.EnvironmentVariables["DOTNET_WATCH"]);
        Assert.Equal("localhost:12345", execution.EnvironmentVariables["DEBUG_SESSION_PORT"]);
        Assert.Equal("preserved", execution.EnvironmentVariables["UNRELATED_SETTING"]);
    }

    public static string CreateScript(DirectoryInfo directory, string name, string unixScript, string windowsScript)
    {
        var path = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, windowsScript.ReplaceLineEndings("\r\n") + "\r\n");
        }
        else
        {
            File.WriteAllText(path, "#!/bin/sh\n" + unixScript.ReplaceLineEndings("\n") + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public static async Task<(string DotnetPath, string SdkVersion, string ApplicationPath)> CreateForkApplicationAsync(
        TemporaryWorkspace workspace, IProcessExecutionFactory factory, CancellationToken cancellationToken)
    {
        var dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var sdkVersion = new List<string>();
        await using (var version = factory.CreateExecution(dotnetPath, ["--version"], env: null, workspace.WorkspaceRoot,
            new() { StandardOutputCallback = sdkVersion.Add, SuppressLogging = true }))
        {
            Assert.True(await version.StartAsync(cancellationToken));
            Assert.Equal(0, await version.WaitForExitAsync(cancellationToken));
        }
        var framework = $"net{Environment.Version.Major}.0";
        var project = Path.Combine(workspace.Path, "ForkApplication.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{{framework}}</TargetFramework><UseAppHost>false</UseAppHost></PropertyGroup>
            </Project>
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Program.cs"), """
            using System;
            using System.Diagnostics;
            using System.Globalization;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            Console.WriteLine("application stdout");
            Console.Error.WriteLine("application stderr");
            if (args[0].StartsWith("chatty", StringComparison.Ordinal))
            {
                using var writer = Process.Start(new ProcessStartInfo("/bin/sh",
                    ["-c", "while true; do echo descendant-output; sleep 0.01; done"]) { StartDetached = true })!;
                File.WriteAllText(args[1], writer.Id.ToString(CultureInfo.InvariantCulture));
                if (args[0] == "chatty-cancel")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            }
            if (args[0] == "pause")
            {
                File.WriteAllText(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            return int.Parse(args[0], CultureInfo.InvariantCulture);
            """, cancellationToken);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using (var build = factory.CreateExecution(dotnetPath, ["build", project, "--nologo", "--verbosity", "quiet"],
            env: null, workspace.WorkspaceRoot, new()
            {
                StandardOutputCallback = diagnostics.Enqueue,
                StandardErrorCallback = diagnostics.Enqueue,
                SuppressLogging = true
            }))
        {
            Assert.True(await build.StartAsync(cancellationToken));
            Assert.True(await build.WaitForExitAsync(cancellationToken) == 0, string.Join('\n', diagnostics));
        }
        return (dotnetPath, Assert.Single(sdkVersion), Path.Combine(workspace.Path, "bin", "Debug", framework, "ForkApplication.dll"));
    }

    public static async Task<int> WaitForProcessIdAsync(string pidFile, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (File.Exists(pidFile))
            {
                try
                {
                    using var stream = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = await reader.ReadToEndAsync(cancellationToken);
                    if (int.TryParse(text.Trim(), out var pid))
                    {
                        return pid;
                    }
                }
                catch (IOException)
                {
                    // The process may still be writing the file. Retry until the caller's timeout expires.
                }
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    public static bool WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessExited(pid))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    public static bool IsProcessExited(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return probe.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public static void TryKillProcess(int pid)
    {
        if (IsProcessExited(pid))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
