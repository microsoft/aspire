// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsPipeBrowserProcessLauncherTests
{
    [Fact]
    public void CreatePipeArguments_AppendsRemoteDebuggingPipeArgument()
    {
        var originalArguments = new[]
        {
            "--user-data-dir=/tmp/aspire-browser",
            "--new-window",
            "about:blank"
        };

        var pipeArguments = BrowserLogsPipeBrowserProcessLauncher.CreatePipeArguments(originalArguments);

        Assert.Equal(
            [
                "--user-data-dir=/tmp/aspire-browser",
                "--new-window",
                "about:blank",
                "--remote-debugging-pipe"
            ],
            pipeArguments);
        Assert.DoesNotContain("--remote-debugging-pipe", originalArguments);
    }

    [Fact]
    public void BuildWindowsCommandLine_QuotesExecutableAndArguments()
    {
        var commandLine = BrowserLogsPipeBrowserProcessLauncher.BuildWindowsCommandLine(
            @"C:\Program Files\Browser\chrome.exe",
            [
                "--flag",
                @"--user-data-dir=C:\Users\Test User\Profile",
                "quote\"value"
            ]);

        Assert.Equal("\"C:\\Program Files\\Browser\\chrome.exe\" --flag \"--user-data-dir=C:\\Users\\Test User\\Profile\" \"quote\\\"value\"", commandLine);
    }

    [Fact]
    public async Task Start_MapsPosixPipeDescriptorsToChildFd3AndFd4()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var process = BrowserLogsPipeBrowserProcessLauncher.Start(
            "/bin/sh",
            [
                "-c",
                "IFS= read -r line <&3; printf '%s' \"$line\" >&4",
                "browserlogs-pipe-test"
            ]);

        await process.BrowserInput.WriteAsync(Encoding.UTF8.GetBytes("ping\n")).DefaultTimeout();
        await process.BrowserInput.FlushAsync().DefaultTimeout();

        var response = await ReadExactlyAsync(process.BrowserOutput, byteCount: 4).DefaultTimeout();
        Assert.Equal("ping", Encoding.UTF8.GetString(response));

        var result = await process.ProcessTask.DefaultTimeout();
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int byteCount)
    {
        var buffer = new byte[byteCount];
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0)
            {
                throw new EndOfStreamException("The process exited before the expected pipe response was read.");
            }

            offset += read;
        }

        return buffer;
    }
}
