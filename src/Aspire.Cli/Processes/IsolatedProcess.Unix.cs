// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Cli.Processes;

internal sealed partial class IsolatedProcess
{
    private static async Task<StartedProcess> StartDetachedUnixAsync(
        IsolatedProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        if (startInfo.DetachedUnixLauncherPath is null)
        {
            throw new InvalidOperationException("Unix detached process launch requires a DCP executable path.");
        }

        var dcpStartInfo = new ProcessStartInfo
        {
            FileName = startInfo.DetachedUnixLauncherPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = startInfo.WorkingDirectory
        };

        dcpStartInfo.ArgumentList.Add("fork-process");
        dcpStartInfo.ArgumentList.Add("--monitor");
        dcpStartInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        dcpStartInfo.ArgumentList.Add("--monitor-identity-time");
        dcpStartInfo.ArgumentList.Add(ProcessTreeGracefulShutdownService.FormatDcpProcessStartTime(GetCurrentProcessDcpMonitorStartTime()));
        dcpStartInfo.ArgumentList.Add("--");
        dcpStartInfo.ArgumentList.Add(startInfo.FileName);
        foreach (var arg in startInfo.ArgumentList)
        {
            dcpStartInfo.ArgumentList.Add(arg);
        }

        ProcessEnvironment.ApplyTo(dcpStartInfo, startInfo.GetEnvironmentForSpawn());

        cancellationToken.ThrowIfCancellationRequested();

        var dcpProcess = Process.Start(dcpStartInfo)
            ?? throw new InvalidOperationException("Failed to start DCP fork-process.");

        var stderrTask = dcpProcess.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutLineTask = dcpProcess.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();

        try
        {
            // Once DCP has started, wait for it to report the detached child PID even if the caller
            // cancels. Without the PID, callers cannot clean up a child that was already forked.
            var stdoutLine = await stdoutLineTask.ConfigureAwait(false);
            if (stdoutLine is null)
            {
                await dcpProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"DCP fork-process did not return a child process ID. DCP fork-process exited with code {dcpProcess.ExitCode}. stderr: '{stderr.Trim()}'");
            }

            var trimmedStdout = stdoutLine.Trim();
            // DCP fork-process writes only the detached child PID followed by a newline, for example:
            //   12345
            if (!int.TryParse(trimmedStdout, NumberStyles.None, CultureInfo.InvariantCulture, out var childPid))
            {
                throw new InvalidOperationException($"DCP fork-process did not return a valid child process ID. stdout: '{trimmedStdout}'");
            }

            ObserveDcpForkProcessStderr(stderrTask);

            Process? childProcess;
            try
            {
                childProcess = Process.GetProcessById(childPid);
            }
            catch (ArgumentException)
            {
                // A short-lived detached child can exit and be reaped by the DCP monitor between
                // DCP printing its PID and this parent opening a Process handle. In that case the
                // monitor process is the only remaining handle that can report the child's exit code.
                await dcpProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return new StartedProcess(
                    dcpProcess,
                    TextReader.Null,
                    TextReader.Null,
                    ExtraDispose: null,
                    ExitCodeProvider: () => dcpProcess.ExitCode,
                    HasExitedProvider: () => true,
                    WaitForExitProvider: dcpProcess.WaitForExitAsync,
                    UseProvidedStartTime: true,
                    ProcessId: childPid);
            }

            return new StartedProcess(
                childProcess,
                TextReader.Null,
                TextReader.Null,
                ExtraDispose: () =>
                {
                    dcpProcess.Dispose();
                    return ValueTask.CompletedTask;
                },
                ExitCodeProvider: () => dcpProcess.HasExited ? dcpProcess.ExitCode : childProcess.ExitCode,
                HasExitedProvider: () => dcpProcess.HasExited,
                WaitForExitProvider: dcpProcess.WaitForExitAsync,
                StartTime: GetStartTime(childProcess),
                ProcessId: childPid);
        }
        catch
        {
            if (!dcpProcess.HasExited)
            {
                dcpProcess.Kill(entireProcessTree: true);
                await dcpProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            dcpProcess.Dispose();
            throw;
        }
    }

    private static void ObserveDcpForkProcessStderr(Task<string> stderrTask)
    {
        _ = stderrTask.ContinueWith(
            static (completedTask, state) =>
            {
                if (!completedTask.IsCompletedSuccessfully)
                {
                    _ = completedTask.Exception;
                    return;
                }

                _ = completedTask.Result;
            },
            state: null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static DateTimeOffset GetCurrentProcessDcpMonitorStartTime()
    {
        if (OperatingSystem.IsLinux())
        {
            // DCP's Linux process identity is the /proc/<pid>/stat start tick converted to
            // milliseconds since boot and represented as Go's zero time plus that duration.
            // Build the same timestamp instead of using Process.StartTime, which is derived from
            // wall-clock boot time and can drift after clock adjustments.
            return DateTimeOffset.MinValue.AddMilliseconds(ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds());
        }

        return ProcessStartTimeHelper.GetCurrentProcessStartTime();
    }
}
