// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Unix implementation using DCP's <c>fork-process</c> helper.
    /// </summary>
    private static async Task<DetachedProcess> StartUnix(
        string dcpPath,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dcpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = workingDirectory
        };

        startInfo.ArgumentList.Add("fork-process");
        startInfo.ArgumentList.Add("--monitor");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--monitor-identity-time");
        startInfo.ArgumentList.Add(ProcessTreeGracefulShutdownService.FormatDcpProcessStartTime(GetCurrentProcessDcpMonitorStartTime()));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(fileName);
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Remove specified environment variables from the child process.
        // Accessing startInfo.Environment auto-populates from the current process.
        if (shouldRemoveEnvironmentVariable is not null)
        {
            var keysToRemove = new List<string>();
            foreach (var key in startInfo.Environment.Keys)
            {
                if (shouldRemoveEnvironmentVariable(key))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                startInfo.Environment.Remove(key);
            }
        }

        // Add additional environment variables to the child process without mutating the parent.
        if (additionalEnvironmentVariables is not null)
        {
            foreach (var (key, value) in additionalEnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var dcpProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start DCP fork-process.");

        // Read stderr concurrently so DCP cannot block while reporting a launch error.
        var stderrTask = dcpProcess.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutLineTask = dcpProcess.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();

        try
        {
            // Once DCP has started, wait for it to report the detached child PID even if the caller
            // cancels. Without the PID, AppHostLauncher cannot clean up a child that was already forked.
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

            ObserveDcpForkProcessStderr(stderrTask, logger);
            return new DetachedProcess(Process.GetProcessById(childPid), dcpProcess);
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

    private static void ObserveDcpForkProcessStderr(Task<string> stderrTask, ILogger? logger)
    {
        _ = stderrTask.ContinueWith(
            static (completedTask, state) =>
            {
                var logger = (ILogger?)state;
                if (completedTask.IsFaulted)
                {
                    logger?.LogDebug(completedTask.Exception, "Failed to read DCP fork-process stderr.");
                    _ = completedTask.Exception;
                    return;
                }

                if (completedTask.IsCanceled)
                {
                    return;
                }

                var stderr = completedTask.Result.Trim();
                if (stderr.Length > 0)
                {
                    logger?.LogDebug("DCP fork-process stderr: {DcpStderr}", stderr);
                }
            },
            logger,
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
