// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Cli.Tests.TestServices;

internal static class ProcessTestHelpers
{
    public static string CreateScript(DirectoryInfo directory, string name, string unixScript, string windowsScript)
    {
        var path = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, windowsScript.ReplaceLineEndings("\r\n") + "\r\n");
        }
        else
        {
            WriteUnixExecutable(path, "#!/bin/sh\n" + unixScript.ReplaceLineEndings("\n") + "\n");
        }

        return path;
    }

    // Tests run in parallel and fork processes at any time. A child forked while this process holds a write handle
    // to the script inherits it until exec, and Linux fails exec of a file open for writing with ETXTBSY
    // ("Text file busy"). Writing from a short-lived /bin/sh child keeps the write handle out of the test host.
    private static void WriteUnixExecutable(string path, string contents)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("cat > \"$1\" && chmod 700 \"$1\"");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start /bin/sh.");
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(contents);
        process.StandardInput.Close();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Writing executable '{path}' failed with exit code {process.ExitCode}: {stderr.GetAwaiter().GetResult()}");
        }
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
