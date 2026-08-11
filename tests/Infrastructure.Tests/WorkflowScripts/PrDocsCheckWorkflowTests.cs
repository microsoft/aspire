// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class PrDocsCheckWorkflowTests(ITestOutputHelper testOutput)
{
    [Fact]
    public void SourceWorkflowBridgesCanonicalBaseIntoSafeOutputs()
    {
        var workflow = ReadWorkflow("pr-docs-check.md");
        var safeOutputs = GetSection(workflow, "^safe-outputs:", "^pre-agent-steps:");
        var customSteps = GetSection(safeOutputs, "^  steps:", "^  create-pull-request:");

        Assert.Contains("Resolve safe-output patch base from canonical agent output", customSteps, StringComparison.Ordinal);
        Assert.Contains(
            "if: contains(needs.agent.outputs.output_types, 'create_pull_request')",
            customSteps,
            StringComparison.Ordinal);
        Assert.Contains("/tmp/gh-aw/agent_output.json", customSteps, StringComparison.Ordinal);
        Assert.Contains("len(create_items) != 1", customSteps, StringComparison.Ordinal);
        Assert.Contains("create_items[0].get(\"base_branch\")", customSteps, StringComparison.Ordinal);
        Assert.Contains(
            "re.fullmatch(r\"main|release/[0-9]+\\.[0-9]+(?:\\.[0-9]+)?\", base_branch)",
            customSteps,
            StringComparison.Ordinal);
        Assert.Contains("github_output.write(f\"branch={base_branch}\\n\")", customSteps, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(customSteps, "actions/checkout@", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Contains(
            "base-branch: ${{ steps.resolve-target.outputs.branch || 'main' }}",
            safeOutputs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledWorkflowBridgesCanonicalBaseBeforeSafeOutputApplication()
    {
        var workflow = ReadWorkflow("pr-docs-check.lock.yml");
        var safeOutputs = GetSection(workflow, "^  safe_outputs:", "^  validate-docs-outcome:");

        var downloadIndex = safeOutputs.IndexOf("Download agent output artifact", StringComparison.Ordinal);
        var resolveIndex = safeOutputs.IndexOf(
            "Resolve safe-output patch base from canonical agent output",
            StringComparison.Ordinal);
        var processIndex = safeOutputs.IndexOf("Process Safe Outputs", StringComparison.Ordinal);

        Assert.True(downloadIndex >= 0, "The compiled safe_outputs job must download canonical agent output.");
        Assert.True(resolveIndex > downloadIndex, "The apply-time base resolver must run after the canonical output download.");
        Assert.True(processIndex > resolveIndex, "The apply-time base resolver must run before safe outputs are applied.");
        Assert.Contains(
            "if: contains(needs.agent.outputs.output_types, 'create_pull_request')",
            safeOutputs,
            StringComparison.Ordinal);
        Assert.Contains("/tmp/gh-aw/agent_output.json", safeOutputs, StringComparison.Ordinal);
        Assert.Contains(
            "\\\"base_branch\\\":\\\"${{ steps.resolve-target.outputs.branch || 'main' }}\\\"",
            safeOutputs,
            StringComparison.Ordinal);
        Assert.Collection(
            Regex.Matches(safeOutputs, "uses: actions/checkout@", RegexOptions.CultureInvariant).Cast<Match>(),
            _ => { },
            _ => { });
    }

    [Fact]
    public void SourceAndCompiledWorkflowGuardDraftedPrBase()
    {
        foreach (var workflowName in new[] { "pr-docs-check.md", "pr-docs-check.lock.yml" })
        {
            var workflow = ReadWorkflow(workflowName);
            var validationJob = GetSection(
                workflow,
                "^  validate-docs-outcome:",
                workflowName.EndsWith(".md", StringComparison.Ordinal)
                    ? "^safe-outputs:"
                    : "\\z");

            Assert.Contains("Resolve drafted PR base", validationJob, StringComparison.Ordinal);
            Assert.Contains("if: needs.safe_outputs.outputs.created_pr_url != ''", validationJob, StringComparison.Ordinal);
            var urlValidationIndex = validationJob.IndexOf(
                @"^https://github\.com/microsoft/aspire\.dev/pull/([1-9][0-9]*)$",
                StringComparison.Ordinal);
            var lookupIndex = validationJob.IndexOf(
                "/repos/microsoft/aspire.dev/pulls/",
                StringComparison.Ordinal);
            Assert.True(urlValidationIndex >= 0, "The drafted PR URL must be validated.");
            Assert.True(lookupIndex > urlValidationIndex, "The drafted PR URL must be validated before the GitHub lookup.");
            Assert.Contains("--jq '.base.ref // \"\"'", validationJob, StringComparison.Ordinal);
            Assert.Contains("--created-pr-base", validationJob, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task PythonTestsPassOnWindows() => PythonTestsPass("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task PythonTestsPassOnUnix() => PythonTestsPass("python3");

    private async Task PythonTestsPass(string python)
    {
        var startInfo = new ProcessStartInfo(python)
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("unittest");
        startInfo.ArgumentList.Add("discover");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(".github/workflows/pr-docs-check");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("test_*.py");
        startInfo.ArgumentList.Add("-v");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {python}.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        testOutput.WriteLine(stdout);
        testOutput.WriteLine(stderr);

        Assert.True(
            process.ExitCode == 0,
            $"{python} exited with code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private static string GetSection(string text, string startPattern, string endPattern)
    {
        var match = Regex.Match(
            text,
            $"(?ms){startPattern}\\r?\\n.*?(?={endPattern})",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not find workflow section starting with '{startPattern}'.");
        return match.Value;
    }
}
