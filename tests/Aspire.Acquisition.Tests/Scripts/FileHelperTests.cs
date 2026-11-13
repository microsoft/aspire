// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

[SkipOnPlatform(TestPlatforms.Windows, "Executable mock scripts are only exec'd directly on Unix")]
[UnsupportedOSPlatform("windows")]
public class FileHelperTests
{
    // Mock executables must be written with FileHelper.WriteExecutableFileAsync so the test host never holds a
    // writable descriptor that a concurrently forked child could inherit (ETXTBSY, see
    // https://github.com/microsoft/aspire/issues/18732). This checks the helper's contract: exact UTF-8 contents
    // without a BOM, owner-only executable mode, and a file that can be exec'd immediately, including when it
    // replaces an existing non-executable file.
    [Fact]
    public async Task WriteExecutableFileAsync_WritesExecutableScriptThatRunsImmediately()
    {
        using var env = new TestEnvironment();
        var path = Path.Combine(env.TempDirectory, "mock tool");
        // Seed the stale file through the helper too, so this test never holds a writable descriptor for a file
        // it later execs. Then clear the execute bit so the assertions below prove the helper sets it again.
        await FileHelper.WriteExecutableFileAsync(path, "stale");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        const string Contents = "#!/bin/sh\nprintf 'ok-\\303\\251 %s' \"$1\"\n";
        await FileHelper.WriteExecutableFileAsync(path, Contents);

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Contents), await File.ReadAllBytesAsync(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));

        var startInfo = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("arg");
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("ok-é arg", output);
    }
}
