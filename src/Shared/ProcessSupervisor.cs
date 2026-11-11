// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Contains a command in a guardian process that monitors its owner's stable process identity.
/// </summary>
internal static partial class ProcessSupervisor
{
    internal const string CommandVariable = "ASPIRE_PROCESS_SUPERVISOR_COMMAND";
    private const string ParentIdVariable = "ASPIRE_PROCESS_SUPERVISOR_PARENT_PID";
    private const string ParentStartedVariable = "ASPIRE_PROCESS_SUPERVISOR_PARENT_STARTED";
    private const string ExitCodePathVariable = "ASPIRE_PROCESS_SUPERVISOR_EXIT_CODE_PATH";
    private const string TerminationTimeoutVariable = "ASPIRE_PROCESS_SUPERVISOR_TERMINATION_TIMEOUT_MS";

    internal static ProcessStartInfo CreateStartInfo(ProcessStartInfo runtimeStartInfo, string? exitCodePath = null)
        => CreateStartInfo(runtimeStartInfo, exitCodePath, TimeSpan.FromSeconds(5));

    internal static ProcessStartInfo CreateStartInfo(ProcessStartInfo runtimeStartInfo, string? exitCodePath, TimeSpan terminationTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(terminationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(terminationTimeout.TotalMilliseconds, uint.MaxValue - 1d);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the owner executable for process supervision.");
        var arguments = Environment.GetCommandLineArgs();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtimeStartInfo.WorkingDirectory,
            CreateNoWindow = runtimeStartInfo.CreateNoWindow,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
#if NET11_0_OR_GREATER
        startInfo.InheritedHandles = [];
#endif
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
        // The guardian owns cleanup independently of the normal CLI watchdog.
        // That watchdog must not force-exit the guardian before it reaps descendants.
        startInfo.Environment.Remove(KnownConfigNames.CliProcessId);
        startInfo.Environment.Remove(KnownConfigNames.CliProcessStarted);
        startInfo.Environment.Remove(KnownConfigNames.CliProcessStartedStable);
        startInfo.Environment[CommandVariable] = JsonSerializer.Serialize(new LaunchCommand
        {
            FileName = runtimeStartInfo.FileName,
            Arguments = runtimeStartInfo.Arguments,
            ArgumentList = runtimeStartInfo.ArgumentList.ToArray()
        }, ProcessSupervisorJsonContext.Default.LaunchCommand);
        startInfo.Environment[ParentIdVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[ParentStartedVariable] = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(Environment.ProcessId)?
            .ToString(CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Cannot inspect the owner's process identity.");
        startInfo.Environment.Remove(ExitCodePathVariable);
        startInfo.Environment[TerminationTimeoutVariable] = terminationTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
        if (exitCodePath is not null)
        {
            startInfo.Environment[ExitCodePathVariable] = exitCodePath;
        }

        return startInfo;
    }

    internal static bool IsSupervisor => Environment.GetEnvironmentVariable(CommandVariable) is not null;

    internal static async Task RunAsync()
    {
        var logger = new ProcessSupervisorLogger(Console.Error);
        // The private handoff is {"FileName":"node","Arguments":"","ArgumentList":["--import","tsx","host.ts"]}.
        // Preserve raw Arguments as well: Windows batch shims use cmd's own quoting rules.
        var command = JsonSerializer.Deserialize(
            Environment.GetEnvironmentVariable(CommandVariable)!, ProcessSupervisorJsonContext.Default.LaunchCommand)
            ?? throw new InvalidOperationException("Missing process supervisor command.");
        var parentId = int.Parse(Environment.GetEnvironmentVariable(ParentIdVariable)!, CultureInfo.InvariantCulture);
        var parentStarted = long.Parse(Environment.GetEnvironmentVariable(ParentStartedVariable)!, CultureInfo.InvariantCulture);
        var exitCodePath = Environment.GetEnvironmentVariable(ExitCodePathVariable);
        var terminationTimeout = TimeSpan.FromMilliseconds(double.Parse(
            Environment.GetEnvironmentVariable(TerminationTimeoutVariable)!, CultureInfo.InvariantCulture));
        // Establish containment before spawning anything. Doing this inside the
        // guardian also supports net10 AppHost servers without net11 Process APIs.
        // https://pubs.opengroup.org/onlinepubs/9799919799/functions/setsid.html
        if (!OperatingSystem.IsWindows() && getpgrp() != Environment.ProcessId && setsid() < 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Could not isolate the process supervisor.");
        }
        using var job = OperatingSystem.IsWindows() ? CreateWindowsJob() : null;

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false
        };
        foreach (var argument in command.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Remove(CommandVariable);
        startInfo.Environment.Remove(ParentIdVariable);
        startInfo.Environment.Remove(ParentStartedVariable);
        startInfo.Environment.Remove(ExitCodePathVariable);
        startInfo.Environment.Remove(TerminationTimeoutVariable);
        // Inherit the guardian's pipes rather than adding another output pump. Diagnostics go
        // directly to the owner even when the guardian must kill its own Unix process group.
        var process = new ChildProcess(
            startInfo, logger, new ChildProcessOptions { TerminationTimeout = terminationTimeout }, OperatingSystem.IsWindows());
        await using var processLifetime = process.ConfigureAwait(false);

        try
        {
            if (!ProcessStartTimeHelper.IsProcessRunning(parentId, parentStarted))
            {
                throw new InvalidOperationException("The owner exited before the supervised command could start.");
            }
            var parentExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = ParentProcessLivenessMonitor.Start(parentId, parentStarted, _ =>
            {
                parentExited.TrySetResult();
                return Task.CompletedTask;
            });
            await using var monitorLifetime = monitor.ConfigureAwait(false);
            if (!await process.StartAsync(CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Could not start supervised command '{command.FileName}'.");
            }

            logger.LogInformation("Started '{Command}' (runtime PID {Pid}, owner PID {OwnerPid}, cwd '{Directory}').",
                command.FileName, process.ProcessId, parentId, Environment.CurrentDirectory);
            // The guardian must remain alive while a signalled runtime performs graceful
            // cleanup. Unix signals target the guardian; Windows console events reach both.
            using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                ProcessSignaler.RequestGracefulShutdown(process.ProcessId, process.StartTime, logger);
            });
            using var sigint = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                ProcessSignaler.RequestGracefulShutdown(process.ProcessId, process.StartTime, logger);
            });
            ConsoleCancelEventHandler cancelHandler = (_, args) => args.Cancel = true;
            if (OperatingSystem.IsWindows())
            {
                Console.CancelKeyPress += cancelHandler;
            }
            try
            {
                var exited = process.WaitForRootExitAsync(CancellationToken.None);
                if (await Task.WhenAny(exited, parentExited.Task).ConfigureAwait(false) != exited)
                {
                    logger.LogWarning("Owner process {OwnerPid} exited; terminating its supervised process scope.", parentId);
                    return;
                }

                await exited.ConfigureAwait(false);
                logger.Log(process.ExitCode == 0 ? LogLevel.Information : LogLevel.Warning,
                    "Supervised command '{Command}' exited with code {ExitCode}.", command.FileName, process.ExitCode);
                if (exitCodePath is not null)
                {
                    // Unix cleanup kills the guardian together with its group, so its OS exit status
                    // is not the command's status. Hand off the status privately before scope teardown;
                    // the owner must still verify cleanup before accepting a successful installation.
                    await File.WriteAllTextAsync(exitCodePath, process.ExitCode.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                Environment.ExitCode = process.ExitCode;
            }
            finally
            {
                if (OperatingSystem.IsWindows())
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Process supervisor failed.");
            throw;
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                // The guardian is the group leader, so this also terminates the guardian.
                // It stays outside user code: a blocked Node event loop or lifecycle script
                // cannot defeat the owner-liveness check or descendant cleanup.
                ChildProcess.KillProcessGroup(Environment.ProcessId);
            }
        }
    }

    [LibraryImport("libc")]
    private static partial int getpgrp();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    internal sealed class LaunchCommand
    {
        public required string FileName { get; init; }
        public required string Arguments { get; init; }
        public required string[] ArgumentList { get; init; }
    }
}

[JsonSerializable(typeof(ProcessSupervisor.LaunchCommand))]
internal sealed partial class ProcessSupervisorJsonContext : JsonSerializerContext;
