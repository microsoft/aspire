// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
#if NETFRAMEWORK
using System.Globalization;
#endif
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Runs an Aspire CLI command with structured arguments and bounded execution.
/// </summary>
public sealed class RunAspireCliCommand : Microsoft.Build.Utilities.Task
{
    private const string ArgumentValueMetadataName = "Value";
    private const int ProcessTerminationTimeoutMilliseconds = 5_000;

    /// <summary>
    /// Gets or sets the executable or command shim to run.
    /// </summary>
    [Required]
    public string FileName { get; set; } = null!;

    /// <summary>
    /// Gets or sets the ordered command arguments.
    /// </summary>
    /// <remarks>
    /// An item can carry its argument in <c>Value</c> metadata when its item identity is used only
    /// to preserve ordering or avoid MSBuild item-list splitting.
    /// </remarks>
    public ITaskItem[] Arguments { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum command duration in milliseconds.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 120_000;

    /// <summary>
    /// Gets the command exit code, or <c>-1</c> when the command did not exit normally.
    /// </summary>
    [Output]
    public int ExitCode { get; set; } = -1;

    /// <summary>
    /// Gets a value indicating whether the command exceeded <see cref="TimeoutMilliseconds"/>.
    /// </summary>
    [Output]
    public bool TimedOut { get; set; }

    /// <summary>
    /// Gets the process launch or timeout failure description.
    /// </summary>
    [Output]
    public string? FailureMessage { get; set; }

    /// <summary>
    /// Overrides process termination for tests that must keep a process alive after termination is requested.
    /// </summary>
    internal Func<Process, bool>? TestTerminateProcess { get; set; }

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(FileName))
        {
            Log.LogError("An executable path is required.");
            return false;
        }

        if (TimeoutMilliseconds <= 0)
        {
            Log.LogError("The command timeout must be greater than zero.");
            return false;
        }

        var arguments = Arguments.Select(GetArgumentValue).ToArray();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments)
        };

        try
        {
            if (!process.Start())
            {
                FailureMessage = $"The command '{FileName}' could not be started.";
                return true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            FailureMessage = $"The command '{FileName}' could not be started: {ex.Message}";
            return true;
        }

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            TimedOut = true;
            var processExited = false;
            if (TestTerminateProcess?.Invoke(process) ?? TerminateProcess(process))
            {
                processExited = process.WaitForExit(ProcessTerminationTimeoutMilliseconds);
                if (!processExited)
                {
                    FailureMessage ??= $"The command '{FileName}' timed out after {TimeoutMilliseconds} milliseconds and did not exit within {ProcessTerminationTimeoutMilliseconds} milliseconds after termination was requested.";
                }
            }

            if (processExited)
            {
                WaitForProcessOutput(standardOutputTask, standardErrorTask);
            }

            // WaitForExit reports only the root process's exit after Kill(entireProcessTree: true).
            // A surviving descendant can retain inherited pipe handles, so timeout cleanup must
            // never wait indefinitely for the redirected readers to reach EOF.
            // See https://learn.microsoft.com/dotnet/api/system.diagnostics.process.kill#remarks.
            LogProcessOutputIfCompleted(standardOutputTask);
            LogProcessOutputIfCompleted(standardErrorTask);
            FailureMessage ??= $"The command timed out after {TimeoutMilliseconds} milliseconds.";
            return true;
        }

        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        LogProcessOutput(standardOutput);
        LogProcessOutput(standardError);

        ExitCode = process.ExitCode;
        return true;
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        ProcessStartInfoHelper.SetCommand(startInfo, FileName, arguments, Path.DirectorySeparatorChar == '\\');

        return startInfo;
    }

    private bool TerminateProcess(Process process)
    {
#if NETFRAMEWORK
        return TerminateProcessTree(process);
#else
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout and termination attempt.
            return true;
        }
        catch (Win32Exception ex)
        {
            FailureMessage = $"The timed-out command '{FileName}' could not be terminated: {ex.Message}";
            return false;
        }
#endif
    }

#if NETFRAMEWORK
    private bool TerminateProcessTree(Process process)
    {
        int processId;
        try
        {
            processId = process.Id;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout and termination attempt.
            return true;
        }

        var taskKillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        using var taskKill = new Process
        {
            StartInfo = new ProcessStartInfo(
                taskKillPath,
                $"/PID {processId.ToString(CultureInfo.InvariantCulture)} /T /F")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            if (!taskKill.Start())
            {
                FailureMessage = $"The process-tree terminator '{taskKillPath}' could not be started.";
                return false;
            }

            // taskkill is part of Windows and /T terminates the specified process and its descendants.
            // Resolve it from the system directory so a restricted or test-modified PATH cannot redirect it.
            // See https://learn.microsoft.com/windows-server/administration/windows-commands/taskkill.
            var standardOutputTask = taskKill.StandardOutput.ReadToEndAsync();
            var standardErrorTask = taskKill.StandardError.ReadToEndAsync();

            if (!taskKill.WaitForExit(ProcessTerminationTimeoutMilliseconds))
            {
                try
                {
                    taskKill.Kill();
                }
                catch (InvalidOperationException)
                {
                    // The helper exited between the timeout and termination attempt.
                }

                if (taskKill.HasExited || taskKill.WaitForExit(ProcessTerminationTimeoutMilliseconds))
                {
                    _ = standardOutputTask.GetAwaiter().GetResult();
                    _ = standardErrorTask.GetAwaiter().GetResult();
                }

                FailureMessage = $"The process-tree terminator '{taskKillPath}' did not exit within {ProcessTerminationTimeoutMilliseconds} milliseconds.";
                return false;
            }

            var standardOutput = standardOutputTask.GetAwaiter().GetResult();
            var standardError = standardErrorTask.GetAwaiter().GetResult();
            if (taskKill.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                FailureMessage = $"The process-tree terminator '{taskKillPath}' exited with code {taskKill.ExitCode}." +
                    (string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            FailureMessage = $"The process-tree terminator '{taskKillPath}' failed: {ex.Message}";
            return false;
        }
    }
#endif

    private void LogProcessOutput(string output)
    {
        if (!string.IsNullOrEmpty(output))
        {
            // Setup output is diagnostic only. Logging it as a message prevents a recovered attempt
            // from becoming a warning-as-error failure because the tool wrote warning-like text.
            Log.LogMessage(MessageImportance.Low, "{0}", output);
        }
    }

    private static void WaitForProcessOutput(Task<string> standardOutputTask, Task<string> standardErrorTask)
    {
        try
        {
            _ = Task.WaitAll([standardOutputTask, standardErrorTask], ProcessTerminationTimeoutMilliseconds);
        }
        catch
        {
            // Draining outputs is best-effort and a failure to do so (including a timeout) should not interrupt the caller.
        }
    }

    private void LogProcessOutputIfCompleted(Task<string> outputTask)
    {
        if (outputTask.Status == TaskStatus.RanToCompletion)
        {
            var output = outputTask.GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(output))
            {
                LogProcessOutput(output);
            }

            return;
        }

        // "Observe" task exceptions (for tasks that exceed the wait timeout and eventually fail) so that
        // UnobservedTaskException (if enabled) does not bring down the process.
        _ = outputTask.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string GetArgumentValue(ITaskItem argument)
    {
        var value = argument.GetMetadata(ArgumentValueMetadataName);
        return string.IsNullOrEmpty(value) ? argument.ItemSpec : value;
    }

}
