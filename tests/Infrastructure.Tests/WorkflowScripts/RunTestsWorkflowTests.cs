// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class RunTestsWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string WorkflowRelativePath = ".github/workflows/run-tests.yml";
    private const string UnixStepName = "Run tests (Linux/macOS)";
    private const string WindowsStepName = "Run tests (Windows)";

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [RequiresTools(["bash"])]
    public async Task UnixDotnetTestExitCodeClassificationPreservesFailures(int testExitCode, int expectedExitCode)
    {
        var scriptPath = Path.Combine(_workspace.Path, $"run-tests-{testExitCode}.sh");
        await File.WriteAllTextAsync(scriptPath, BuildUnixClassificationScript(testExitCode));

        var result = await RunProcessAsync("bash", ["-e", scriptPath]);

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
        var scriptPath = Path.Combine(_workspace.Path, $"run-tests-{testExitCode}.ps1");
        await File.WriteAllTextAsync(scriptPath, BuildWindowsClassificationScript(testExitCode));

        var result = await RunProcessAsync("pwsh", ["-NoProfile", "-File", scriptPath]);

        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    private string BuildUnixClassificationScript(int testExitCode)
    {
        var script = GetRunStepScript(UnixStepName);
        var start = script.IndexOf("# Persist the MTP exit code", StringComparison.Ordinal);
        var end = script.LastIndexOf("exit $TEST_EXIT_CODE", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find the exit-code persistence block in '{UnixStepName}'.");
        Assert.True(end >= start, $"Could not find the final exit in '{UnixStepName}'.");

        var classification = script[start..(end + "exit $TEST_EXIT_CODE".Length)]
            .Replace("${{ github.workspace }}", _workspace.Path, StringComparison.Ordinal)
            .Replace("${{ inputs.ignoreTestFailures }}", "false", StringComparison.Ordinal);

        var normalizationStart = script.IndexOf("# `--ignore-exit-code 8`", StringComparison.Ordinal);
        Assert.True(normalizationStart >= 0, $"Could not find the exit-code normalization block in '{UnixStepName}'.");
        classification = script[normalizationStart..start] + classification;

        return $"TEST_EXIT_CODE={testExitCode}\n{classification}\n";
    }

    private string BuildWindowsClassificationScript(int testExitCode)
    {
        var lines = GetRunStepScript(WindowsStepName).ReplaceLineEndings("\n").Split('\n');
        var normalizationStart = Array.FindIndex(lines, line => line.Contains("# `--ignore-exit-code 8`", StringComparison.Ordinal));
        var normalizationEnd = Array.FindIndex(lines, normalizationStart, line => line.Trim() == "}");
        var persistenceStart = Array.FindIndex(lines, line => line.Contains("# Persist the MTP exit code", StringComparison.Ordinal));
        var finalExit = Array.FindLastIndex(lines, line => line.Trim() == "exit $testExitCode");
        Assert.True(normalizationStart >= 0, $"Could not find the exit-code normalization block in '{WindowsStepName}'.");
        Assert.True(normalizationEnd >= normalizationStart, $"Could not find the end of the exit-code normalization block in '{WindowsStepName}'.");
        Assert.True(persistenceStart > normalizationEnd, $"Could not find the exit-code persistence block in '{WindowsStepName}'.");
        Assert.True(finalExit >= persistenceStart, $"Could not find the final exit in '{WindowsStepName}'.");

        var classification = string.Join(
            '\n',
            lines[normalizationStart..(normalizationEnd + 1)]
                .Concat(lines[persistenceStart..(finalExit + 1)]))
            .Replace("${{ github.workspace }}", _workspace.Path, StringComparison.Ordinal)
            .Replace("${{ inputs.ignoreTestFailures }}", "false", StringComparison.Ordinal);

        return $"$testExitCode = {testExitCode}\n{classification}\n";
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = _workspace.Path;
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

    private static string GetRunStepScript(string stepName)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(RepoRoot.Path, WorkflowRelativePath));
        yaml.Load(reader);

        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("jobs")];
        var testJob = (YamlMappingNode)jobs.Children[new YamlScalarNode("test")];
        var steps = ((YamlSequenceNode)testJob.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>();
        var runStep = Assert.Single(steps, step => Scalar(step, "name") == stepName);

        return Scalar(runStep, "run") ?? throw new InvalidOperationException($"The '{stepName}' step has no run script.");
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value : null;

    public void Dispose() => _workspace.Dispose();

    private sealed record CommandResult(int ExitCode, string Output);
}
