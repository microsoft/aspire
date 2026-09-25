// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Guards how the nightly Deployment E2E workflow classifies the exit code of the single
/// matrix test it runs, in particular the "zero tests ran" code produced by a dynamically
/// skipped scenario.
/// </summary>
public sealed class DeploymentTestExitCodeScriptTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string s_classifierPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "classify-deployment-test-exit-code.sh");

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData(0, false)]   // all tests passed
    [InlineData(8, false)]   // MTP "zero tests ran": the only test was dynamically skipped
    [InlineData(2, true)]    // a test failed
    [InlineData(7, true)]    // the test host crashed
    [RequiresTools(["bash"])]
    public async Task ClassificationMarksOnlyGenuineFailures(int testExitCode, bool expectFailure)
    {
        var outputFile = Path.Combine(_workspace.Path, "github-output.txt");
        var result = await RunTestStepAsync(testExitCode, outputFile);

        Assert.Equal(0, result.ExitCode);
        var stepOutputs = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile) : string.Empty;
        if (expectFailure)
        {
            Assert.Equal($"test_failed=true{Environment.NewLine}", stepOutputs);
        }
        else
        {
            Assert.Equal(string.Empty, stepOutputs);
        }
    }

    private async Task<CommandResult> RunTestStepAsync(int testExitCode, string outputFile)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(s_classifierPath);
        process.StartInfo.ArgumentList.Add(testExitCode.ToString());
        process.StartInfo.WorkingDirectory = _workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["GITHUB_OUTPUT"] = outputFile;

        process.Start();

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        var result = new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
        output.WriteLine(result.Output);

        return result;
    }

    public void Dispose() => _workspace.Dispose();

    private sealed record CommandResult(int ExitCode, string Output);
}
