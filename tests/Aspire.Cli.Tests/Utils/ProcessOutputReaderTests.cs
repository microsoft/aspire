// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Utils;

public class ProcessOutputReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAllText_MatchesStreamReaderDefaultEncoding(bool async)
    {
        var startInfo = ProcessTestHelpers.CreateOutputProcessStartInfo([0x41, 0x80, 0xFF], [0x42, 0xFE, 0x81]);
        using var original = Process.Start(startInfo)!;
        var originalOutputTask = original.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var originalErrorTask = original.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(originalOutputTask, originalErrorTask, original.WaitForExitAsync(TestContext.Current.CancellationToken)).DefaultTimeout();
        using var process = Process.Start(startInfo)!;

        var result = async
            ? await ProcessOutputReader.ReadAllTextAsync(process, TestContext.Current.CancellationToken).DefaultTimeout()
            : await Task.Run(() => ProcessOutputReader.ReadAllText(process)).DefaultTimeout();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(await originalOutputTask, result.StandardOutput);
        Assert.Equal(await originalErrorTask, result.StandardError);
    }

    [Theory]
    [InlineData(false, 65001)]
    [InlineData(true, 65001)]
    [InlineData(false, 1200)]
    [InlineData(true, 1200)]
    [InlineData(false, 1201)]
    [InlineData(true, 1201)]
    [InlineData(false, 12000)]
    [InlineData(true, 12000)]
    [InlineData(false, 12001)]
    [InlineData(true, 12001)]
    public async Task ReadAllText_DetectsByteOrderMarks(bool async, int codePage)
    {
        var encoding = Encoding.GetEncoding(codePage);
        const string output = "version-\u00E9\n";
        const string error = "diagnostic-\u2603\n";
        var startInfo = ProcessTestHelpers.CreateOutputProcessStartInfo(
            [.. encoding.GetPreamble(), .. encoding.GetBytes(output)],
            [.. encoding.GetPreamble(), .. encoding.GetBytes(error)]);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(startInfo)!;

        var result = async
            ? await ProcessOutputReader.ReadAllTextAsync(process, TestContext.Current.CancellationToken).DefaultTimeout()
            : await Task.Run(() => ProcessOutputReader.ReadAllText(process)).DefaultTimeout();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(output, result.StandardOutput);
        Assert.Equal(error, result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAllText_UsesEachStreamsEncodingWithoutBom(bool async)
    {
        const string output = "output-\u00E9";
        const string error = "error-\u2603";
        var startInfo = ProcessTestHelpers.CreateOutputProcessStartInfo(
            Encoding.Unicode.GetBytes(output),
            Encoding.BigEndianUnicode.GetBytes(error));
        startInfo.StandardOutputEncoding = Encoding.Unicode;
        startInfo.StandardErrorEncoding = Encoding.BigEndianUnicode;
        using var process = Process.Start(startInfo)!;

        var result = async
            ? await ProcessOutputReader.ReadAllTextAsync(process, TestContext.Current.CancellationToken).DefaultTimeout()
            : await Task.Run(() => ProcessOutputReader.ReadAllText(process)).DefaultTimeout();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(output, result.StandardOutput);
        Assert.Equal(error, result.StandardError);
    }
}
