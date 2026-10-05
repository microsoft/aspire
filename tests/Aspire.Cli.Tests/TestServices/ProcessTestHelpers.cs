// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Tests.TestServices;

internal static class ProcessTestHelpers
{
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
