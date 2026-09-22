// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

/// <summary>
/// Guards how the nightly Deployment E2E workflow classifies the exit code of the single
/// matrix test it runs, in particular the "zero tests ran" code produced by a dynamically
/// skipped scenario.
/// </summary>
public sealed class DeploymentTestsWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string WorkflowRelativePath = ".github/workflows/deployment-tests.yml";
    private const string RunStepName = "Run deployment test (${{ matrix.shortname }})";

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData(0, false)]   // all tests passed
    [InlineData(8, false)]   // MTP "zero tests ran": the only test was dynamically skipped
    [InlineData(2, true)]    // a test failed
    [InlineData(7, true)]    // the test host crashed
    [RequiresTools(["bash"])]
    public async Task SkippedScenarioIsGreenWhileGenuineFailuresAreRed(int testExitCode, bool expectFailure)
    {
        var outputFile = Path.Combine(_workspace.Path, "github-output.txt");
        var result = await RunTestStepAsync(testExitCode, outputFile);

        Assert.Equal(0, result.ExitCode);
        var stepOutputs = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile) : string.Empty;
        if (expectFailure)
        {
            Assert.Contains("test_failed=true", stepOutputs, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(string.Empty, stepOutputs);
        }
    }

    private async Task<CommandResult> RunTestStepAsync(int testExitCode, string outputFile)
    {
        var scriptPath = Path.Combine(_workspace.Path, "run-step.sh");
        await File.WriteAllTextAsync(scriptPath, BuildStepScript(testExitCode));

        using var process = new Process();
        process.StartInfo.FileName = "bash";
        // GitHub runs `run:` steps with `bash -e {0}`, so the script must be exercised the same way.
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(scriptPath);
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

    /// <summary>
    /// Replaces the `./dotnet.sh test ...` invocation (a single backslash-continued command) with a
    /// no-op that returns <paramref name="testExitCode"/>, so the surrounding classification logic
    /// from the workflow runs verbatim without building or deploying anything.
    /// </summary>
    private static string BuildStepScript(int testExitCode)
    {
        var lines = GetRunStepScript().ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Contains("./dotnet.sh test", StringComparison.Ordinal));
        Assert.True(start >= 0, $"Could not find the test invocation in the '{RunStepName}' step.");

        var end = start;
        while (lines[end].TrimEnd().EndsWith('\\'))
        {
            end++;
        }

        var rewritten = lines[..start]
            .Append($"(exit {testExitCode})")
            .Concat(lines[(end + 1)..]);

        return string.Join('\n', rewritten);
    }

    private static string GetRunStepScript()
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(RepoRoot.Path, WorkflowRelativePath)))
        {
            yaml.Load(reader);
        }

        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
        var deployTest = (YamlMappingNode)jobs.Children[new YamlScalarNode("deploy-test")];
        var steps = ((YamlSequenceNode)deployTest.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>();
        var runStep = Assert.Single(steps, step => Scalar(step, "name") == RunStepName);

        return Scalar(runStep, "run") ?? throw new InvalidOperationException($"The '{RunStepName}' step has no run script.");
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;

    public void Dispose() => _workspace.Dispose();

    private sealed record CommandResult(int ExitCode, string Output);
}
