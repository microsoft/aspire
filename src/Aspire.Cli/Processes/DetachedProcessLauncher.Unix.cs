// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Unix implementation using DCP's <c>fork-process</c> helper.
    /// </summary>
    private static async Task<Process> StartUnix(
        string dcpPath,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Func<string, bool>? shouldRemoveEnvironmentVariable,
        IReadOnlyDictionary<string, string>? additionalEnvironmentVariables,
        CancellationToken cancellationToken)
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

        using var dcpProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start DCP fork-process.");

        // Read both streams concurrently so DCP cannot block while reporting a launch error.
        var stdoutTask = dcpProcess.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = dcpProcess.StandardError.ReadToEndAsync(CancellationToken.None);

        // Once DCP has started, wait for it to report the detached child PID even if the caller
        // cancels. Without the PID, AppHostLauncher cannot clean up a child that was already forked.
        await dcpProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var dcpOutput = $"stdout: '{stdout.Trim()}', stderr: '{stderr.Trim()}'";

        if (dcpProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"DCP fork-process exited with code {dcpProcess.ExitCode}. {dcpOutput}");
        }

        var trimmedStdout = stdout.Trim();
        // DCP fork-process writes only the detached child PID followed by a newline, for example:
        //   12345
        if (!int.TryParse(trimmedStdout, NumberStyles.None, CultureInfo.InvariantCulture, out var childPid))
        {
            throw new InvalidOperationException($"DCP fork-process did not return a valid child process ID. {dcpOutput}");
        }

        return Process.GetProcessById(childPid);
    }
}
