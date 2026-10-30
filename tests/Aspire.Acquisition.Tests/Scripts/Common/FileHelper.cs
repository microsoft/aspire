// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Small file-system helpers shared across test classes.
/// </summary>
internal static class FileHelper
{
    /// <summary>
    /// Marks a file as user-executable on Unix; no-op on Windows.
    /// </summary>
    internal static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Writes a script that the test will execute directly. On Unix the file is written by a short-lived
    /// child process so that the test host never holds a writable descriptor for it.
    /// </summary>
    /// <remarks>
    /// xUnit runs test classes in parallel, so other tests in the host fork child processes at any time.
    /// A child forked while the host has the script open for writing inherits that descriptor until it
    /// calls exec, and exec of the script fails with ETXTBSY ("Text file busy") while any process holds
    /// it open for writing. Closing, chmod-ing or renaming the file in the host does not help because the
    /// inherited descriptor still refers to the same inode.
    /// </remarks>
    internal static async Task WriteExecutableFileAsync(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, contents);
            return;
        }

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
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(contents);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new IOException($"Failed to write executable file '{path}' (exit code {process.ExitCode}): {stderr}");
        }
    }
}
