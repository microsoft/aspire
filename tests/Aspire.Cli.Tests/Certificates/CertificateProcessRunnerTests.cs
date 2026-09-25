// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Certificates;

public class CertificateProcessRunnerTests
{
    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task RunAndCaptureText_DetectsByteOrderMarks(int codePage)
    {
        var encoding = Encoding.GetEncoding(codePage);
        const string output = "certificate-\u00E9";
        const string error = "diagnostic-\u2603";
        var startInfo = ProcessTestHelpers.CreateOutputProcessStartInfo(
            [.. encoding.GetPreamble(), .. encoding.GetBytes(output)],
            [.. encoding.GetPreamble(), .. encoding.GetBytes(error)]);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        var result = await Task.Run(() => CertificateProcessRunner.RunAndCaptureText(startInfo, TestContext.Current.CancellationToken))
            .DefaultTimeout();

        Assert.Equal(output, result.StandardOutput);
        Assert.Equal(error, result.StandardError);
    }

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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RunAndCaptureText_CapturesRedirectedOutputBeforeWaitingForExit(bool redirectOutput, bool redirectError)
    {
        var startInfo = CreateProcessStartInfo();
        using var nullHandle = File.OpenNullHandle();
        startInfo.RedirectStandardOutput = redirectOutput;
        startInfo.RedirectStandardError = redirectError;
        startInfo.StandardOutputHandle = redirectOutput ? null : nullHandle;
        startInfo.StandardErrorHandle = redirectError ? null : nullHandle;

        var result = await Task.Run(() => CertificateProcessRunner.RunAndCaptureText(startInfo))
            .DefaultTimeout();

        var expectedStandardOutput = redirectOutput ? string.Concat(Enumerable.Repeat(StandardOutputLine + Environment.NewLine, LineCount)) : string.Empty;
        var expectedStandardError = redirectError ? string.Concat(Enumerable.Repeat(StandardErrorLine + Environment.NewLine, LineCount)) : string.Empty;

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
