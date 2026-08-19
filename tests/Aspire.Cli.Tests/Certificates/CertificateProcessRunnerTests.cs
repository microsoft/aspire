// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Certificates;

public class CertificateProcessRunnerTests
{
    [Fact]
    public async Task Run_DiscardsRedirectedOutputBeforeWaitingForExit()
    {
        var startInfo = CreateProcessStartInfo();

        var result = await Task.Run(() => CertificateProcessRunner.Run(startInfo))
            .DefaultTimeout();

        Assert.Equal(ExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task RunAndCaptureText_CapturesRedirectedOutputBeforeWaitingForExit()
    {
        var startInfo = CreateProcessStartInfo();

        var result = await Task.Run(() => CertificateProcessRunner.RunAndCaptureText(startInfo))
            .DefaultTimeout();

        var expectedStandardOutput = string.Concat(Enumerable.Repeat(StandardOutputLine + Environment.NewLine, LineCount));
        var expectedStandardError = string.Concat(Enumerable.Repeat(StandardErrorLine + Environment.NewLine, LineCount));

        Assert.Equal(ExitCode, result.ExitCode);
        Assert.Equal(expectedStandardOutput, result.StandardOutput);
        Assert.Equal(expectedStandardError, result.StandardError);
    }

    private const int LineCount = 2048;
    private const int ExitCode = 17;
    private const string StandardOutputLine = "standard-output-0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string StandardErrorLine = "standard-error--0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ProcessStartInfo CreateProcessStartInfo()
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("powershell.exe");
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $"1..{LineCount} | ForEach-Object {{ [Console]::Out.WriteLine('{StandardOutputLine}') }}; " +
                $"1..{LineCount} | ForEach-Object {{ [Console]::Error.WriteLine('{StandardErrorLine}') }}; " +
                $"exit {ExitCode}");
        }
        else
        {
            startInfo = new ProcessStartInfo("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"i=0; while [ \"$i\" -lt {LineCount} ]; do printf '{StandardOutputLine}\\n'; i=$((i+1)); done; " +
                $"i=0; while [ \"$i\" -lt {LineCount} ]; do printf '{StandardErrorLine}\\n' >&2; i=$((i+1)); done; " +
                $"exit {ExitCode}");
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }
}
