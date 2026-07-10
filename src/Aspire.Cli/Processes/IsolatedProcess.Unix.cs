// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Cli.Processes;

internal sealed partial class IsolatedProcess
{
    private static async Task<IsolatedProcess> StartDetachedUnixAsync(
        IsolatedProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? launcherDiagnosticCallback)
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
            var completedTask = await Task.WhenAny(stdoutLineTask, dcpProcess.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
            if (completedTask != stdoutLineTask)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                var stdout = stdoutLineTask.IsCompletedSuccessfully ? stdoutLineTask.Result : null;
                throw new InvalidOperationException($"DCP fork-process exited with code {dcpProcess.ExitCode}. stdout: '{stdout?.Trim()}', stderr: '{stderr.Trim()}'");
            }

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

            ObserveDcpForkProcessStderr(stderrTask, launcherDiagnosticCallback);
            var childProcess = Process.GetProcessById(childPid);
            return WrapStartedProcess(
                startInfo,
                childProcess,
                TextReader.Null,
                TextReader.Null,
                static (_, _) => { },
                static (_, _) => { },
                extraDispose: () =>
                {
                    dcpProcess.Dispose();
                    return ValueTask.CompletedTask;
                },
                exitCodeProvider: () => dcpProcess.HasExited ? dcpProcess.ExitCode : childProcess.ExitCode,
                hasExitedProvider: () => dcpProcess.HasExited,
                waitForExitProvider: dcpProcess.WaitForExitAsync,
                startTime: GetStartTime(childProcess));
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

    private static void ObserveDcpForkProcessStderr(Task<string> stderrTask, Action<string>? launcherDiagnosticCallback)
    {
        _ = stderrTask.ContinueWith(
            static (completedTask, state) =>
            {
                var launcherDiagnosticCallback = (Action<string>?)state;
                if (!completedTask.IsCompletedSuccessfully)
                {
                    _ = completedTask.Exception;
                    return;
                }

                var stderr = completedTask.Result.Trim();
                if (stderr.Length > 0)
                {
                    launcherDiagnosticCallback?.Invoke(stderr);
                }
            },
            launcherDiagnosticCallback,
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
