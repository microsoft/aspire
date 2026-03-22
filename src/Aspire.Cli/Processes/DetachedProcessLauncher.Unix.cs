// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Unix implementation using Process.Start with stdio redirection.
    /// On Linux/macOS, the redirect pipes' original fds are created with O_CLOEXEC,
    /// but dup2 onto fd 0/1/2 clears that flag — so grandchildren DO inherit the pipe
    /// as their stdio. However, since we close the parent's read-end immediately, the
    /// pipe has no reader and writes produce EPIPE (harmless). The key difference from
    /// Windows is that on Unix, only fds 0/1/2 survive exec — no extra handle leakage.
    /// </summary>
    private static Process StartUnix(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start detached process");

        // Close the parent's read-end of the pipes. This means the pipe has no reader,
        // so if the grandchild (AppHost) writes to inherited stdout/stderr, it gets EPIPE
        // which is harmless. The important thing is no caller is blocked waiting on the
        // pipe — unlike Windows where the handle stays open and blocks execSync callers.
        process.StandardOutput.Close();
        process.StandardError.Close();

        return process;
    }
}
