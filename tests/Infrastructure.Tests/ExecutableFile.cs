// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Infrastructure.Tests;

/// <summary>
/// Writes fake executables (for example, a stub <c>gh</c> placed on <c>PATH</c>) that a test runs right away.
/// </summary>
/// <remarks>
/// xUnit runs test classes in parallel, so other tests in this host start processes at any time. A child
/// forked while the host has a file open for writing inherits that descriptor until it calls <c>exec</c>,
/// and Linux fails <c>exec</c> of a file that any process has open for writing with <c>ETXTBSY</c>
/// ("Text file busy"; bash reports exit code 126). On Unix this helper writes and marks the file executable
/// from a short-lived <c>/bin/sh</c> child, so the test host never holds a writable descriptor to the file
/// that another fork could inherit.
/// </remarks>
internal static class ExecutableFile
{
    private const UnixFileMode DefaultMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void Write(string path, string contents, UnixFileMode mode = DefaultMode)
        => WriteAsync(path, contents, mode).GetAwaiter().GetResult();

    public static async Task WriteAsync(string path, string contents, UnixFileMode mode = DefaultMode)
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
        startInfo.ArgumentList.Add("cat > \"$1\" && chmod \"$2\" \"$1\"");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(Convert.ToString((int)mode, 8));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start /bin/sh.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(contents);
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Writing executable '{path}' failed with exit code {process.ExitCode}: {await stderr}");
        }
    }
}
