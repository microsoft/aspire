// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the command and exit-code behavior of the nightly Deployment E2E test step.
/// </summary>
public sealed class DeploymentTestScriptTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string s_runnerPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "run-deployment-test.sh");

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData(0, false)]   // all tests passed
    [InlineData(8, false)]   // MTP "zero tests ran": the only test was dynamically skipped
    [InlineData(2, true)]    // a test failed
    [InlineData(7, true)]    // the test host crashed
    [RequiresTools(["bash"])]
    public async Task RunsDeploymentTestAndMarksOnlyGenuineFailures(int testExitCode, bool expectFailure)
    {
        var outputFile = Path.Combine(_workspace.Path, "github-output.txt");
        var argumentsFile = Path.Combine(_workspace.Path, "dotnet-arguments.txt");
        var fakeDotnetScript = Path.Combine(_workspace.Path, "dotnet.sh");
        await File.WriteAllTextAsync(
            fakeDotnetScript,
            """
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "$FAKE_DOTNET_ARGUMENTS"
            exit "$FAKE_DOTNET_EXIT_CODE"
            """);

        var resultsDirectory = Path.Combine(_workspace.Path, "test results");
        var result = await RunTestStepAsync(
            testExitCode,
            outputFile,
            argumentsFile,
            fakeDotnetScript,
            resultsDirectory);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "test",
                "--project",
                "tests/Aspire.Deployment.EndToEnd.Tests/Aspire.Deployment.EndToEnd.Tests.csproj",
                "-c",
                "Release",
                "--results-directory",
                resultsDirectory,
                "--",
                "--report-trx",
                "--report-trx-filename",
                "Deployment.EndToEnd-ExampleTests.trx",
                "--filter-not-trait",
                "quarantined=true",
                "--filter-class",
                "Aspire.ExampleTests",
            ],
            await File.ReadAllLinesAsync(argumentsFile));

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

    private async Task<CommandResult> RunTestStepAsync(
        int testExitCode,
        string outputFile,
        string argumentsFile,
        string fakeDotnetScript,
        string resultsDirectory)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(s_runnerPath);
        process.StartInfo.ArgumentList.Add(resultsDirectory);
        process.StartInfo.ArgumentList.Add("Deployment.EndToEnd-ExampleTests");
        process.StartInfo.ArgumentList.Add("--filter-class");
        process.StartInfo.ArgumentList.Add("Aspire.ExampleTests");
        process.StartInfo.WorkingDirectory = _workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["GITHUB_OUTPUT"] = outputFile;
        process.StartInfo.Environment["DOTNET_SCRIPT"] = fakeDotnetScript;
        process.StartInfo.Environment["FAKE_DOTNET_ARGUMENTS"] = argumentsFile;
        process.StartInfo.Environment["FAKE_DOTNET_EXIT_CODE"] = testExitCode.ToString();

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
