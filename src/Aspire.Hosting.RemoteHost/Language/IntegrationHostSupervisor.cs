// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Supervises an integration runtime in a separate process, independently of its event loop.
/// </summary>
internal static partial class IntegrationHostSupervisor
{
    private const string CommandVariable = "ASPIRE_INTEGRATION_HOST_SUPERVISOR_COMMAND";
    private const string ParentIdVariable = "ASPIRE_INTEGRATION_HOST_SUPERVISOR_PARENT_PID";
    private const string ParentStartedVariable = "ASPIRE_INTEGRATION_HOST_SUPERVISOR_PARENT_STARTED";

    internal static ProcessStartInfo CreateStartInfo(ProcessStartInfo runtimeStartInfo)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the AppHost server executable for integration host supervision.");
        var arguments = Environment.GetCommandLineArgs();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtimeStartInfo.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // dotnet's managed argv[0] is the application DLL. Native apphosts have
        // the executable itself at argv[0], which must not be passed again.
        var isDotnet = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnet)
        {
            arguments[0] = Path.GetFullPath(arguments[0]);
        }
        foreach (var argument in isDotnet ? arguments : arguments.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (key, value) in runtimeStartInfo.Environment)
        {
            startInfo.Environment[key] = value;
        }
        // This helper belongs to the server, not to the CLI's helper watchdog.
        // Otherwise a CLI crash could force-exit it before it reaps the runtime.
        startInfo.Environment.Remove(KnownConfigNames.CliProcessId);
        startInfo.Environment.Remove(KnownConfigNames.CliProcessStarted);
        startInfo.Environment.Remove(KnownConfigNames.CliProcessStartedStable);
        startInfo.Environment[CommandVariable] = JsonSerializer.Serialize(new LaunchCommand
        {
            FileName = runtimeStartInfo.FileName,
            Arguments = runtimeStartInfo.Arguments,
            ArgumentList = runtimeStartInfo.ArgumentList.ToArray()
        });
        startInfo.Environment[ParentIdVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[ParentStartedVariable] = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(Environment.ProcessId)?
            .ToString(CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Cannot inspect the AppHost server's process identity.");

        return startInfo;
    }

    internal static bool IsSupervisor => Environment.GetEnvironmentVariable(CommandVariable) is not null;

    internal static async Task RunAsync()
    {
        var command = JsonSerializer.Deserialize<LaunchCommand>(Environment.GetEnvironmentVariable(CommandVariable)!)
            ?? throw new InvalidOperationException("Missing integration host supervisor command.");
        var parentId = int.Parse(Environment.GetEnvironmentVariable(ParentIdVariable)!, CultureInfo.InvariantCulture);
        var parentStarted = long.Parse(Environment.GetEnvironmentVariable(ParentStartedVariable)!, CultureInfo.InvariantCulture);
        // Establish containment before spawning anything. Doing this inside the
        // guardian also supports net10 AppHost servers without net11 Process APIs.
        // https://pubs.opengroup.org/onlinepubs/9799919799/functions/setsid.html
        if (!OperatingSystem.IsWindows() && getpgrp() != Environment.ProcessId && setsid() < 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Could not isolate the integration host supervisor.");
        }
        using var job = OperatingSystem.IsWindows() ? CreateWindowsJob() : null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = false
            }
        };
        foreach (var argument in command.ArgumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment.Remove(CommandVariable);
        process.StartInfo.Environment.Remove(ParentIdVariable);
        process.StartInfo.Environment.Remove(ParentStartedVariable);

        var started = false;
        try
        {
            if (!ProcessStartTimeHelper.IsProcessRunning(parentId, parentStarted))
            {
                throw new InvalidOperationException("The AppHost server exited before the integration runtime could start.");
            }
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start integration runtime '{command.FileName}'.");
            }
            started = true;

            var exited = process.WaitForExitAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!exited.IsCompleted)
            {
                if (!ProcessStartTimeHelper.IsProcessRunning(parentId, parentStarted))
                {
                    Console.Error.WriteLine($"AppHost server {parentId} exited; terminating the integration host process scope.");
                    return;
                }

                using var tickCancellation = new CancellationTokenSource();
                var tick = timer.WaitForNextTickAsync(tickCancellation.Token).AsTask();
                if (await Task.WhenAny(exited, tick).ConfigureAwait(false) == exited)
                {
                    tickCancellation.Cancel();
                    await ((Task)tick).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    break;
                }
                await tick.ConfigureAwait(false);
            }

            await exited.ConfigureAwait(false);
            Console.Error.WriteLine($"Integration runtime '{command.FileName}' exited with code {process.ExitCode}.");
            Environment.ExitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Integration host supervisor failed: {ex}");
            throw;
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                if (started && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            else
            {
                // The guardian is the group leader, so this also terminates the guardian.
                // It deliberately stays outside user integration code: a blocked Node
                // event loop cannot defeat the server-liveness check or descendant cleanup.
                IntegrationHostProcess.KillGroup(Environment.ProcessId);
            }
        }
    }

    [LibraryImport("libc")]
    private static partial int getpgrp();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    private sealed class LaunchCommand
    {
        public required string FileName { get; init; }
        public required string Arguments { get; init; }
        public required string[] ArgumentList { get; init; }
    }
}
