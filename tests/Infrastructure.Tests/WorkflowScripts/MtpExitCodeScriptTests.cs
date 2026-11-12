// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class MtpExitCodeScriptTests(ITestOutputHelper output)
{
    private static readonly string s_unixNormalizerPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "normalize-mtp-exit-code.sh");
    private static readonly string s_windowsNormalizerPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "normalize-mtp-exit-code.ps1");

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [RequiresTools(["bash"])]
    public async Task UnixDotnetTestExitCodeClassificationPreservesFailures(int testExitCode, int expectedExitCode)
    {
        var result = await RunProcessAsync("bash", [s_unixNormalizerPath, testExitCode.ToString()]);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [RequiresTools(["pwsh"])]
    public async Task WindowsDotnetTestExitCodeClassificationPreservesFailures(int testExitCode, int expectedExitCode)
    {
        var result = await RunProcessAsync(
            "pwsh",
            ["-NoProfile", "-File", s_windowsNormalizerPath, "-ExitCode", testExitCode.ToString()]);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = RepoRoot.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        var result = new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
        output.WriteLine(result.Output);

        return result;
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
