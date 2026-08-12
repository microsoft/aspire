// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ExtensionReleaseWorkflowTests(ITestOutputHelper testOutput)
{
    private static readonly string s_releaseWorkflowPath = Path.Combine(RepoRoot.Path, ".github", "workflows", "extension-release.yml");
    private static readonly string s_changelogWorkflowPath = Path.Combine(RepoRoot.Path, ".github", "workflows", "extension-changelog.md");
    private static readonly string s_changelogWorkflowLockPath = Path.Combine(RepoRoot.Path, ".github", "workflows", "extension-changelog.lock.yml");
    private static readonly string s_releaseNotesGeneratorPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "extension-release",
        "generate_deterministic_release_notes.py");

    [Fact]
    public void ExtensionReleaseWorkflowUsesSharedGeneratorWithoutSilentCapsOrTruncation()
    {
        var workflow = File.ReadAllText(s_releaseWorkflowPath);

        Assert.Contains("generate_deterministic_release_notes.py", workflow, StringComparison.Ordinal);
        Assert.False(
            workflow.Contains("""printf '%.8000s'""", StringComparison.Ordinal),
            "The workflow must not silently truncate the fallback release notes.");
        Assert.False(
            workflow.Contains("""if [ "$NOTE_COUNT" -ge 8 ]""", StringComparison.Ordinal),
            "The workflow must not silently stop after eight accepted entries.");
    }

    [Fact]
    public void ExtensionChangelogPromptRequiresLocalFullRangeEnumerationAndRejectsPrBodyAsAuthority()
    {
        var prompt = File.ReadAllText(s_changelogWorkflowPath);
        var compiledWorkflow = File.ReadAllText(s_changelogWorkflowLockPath);

        Assert.Contains(
            "The PR body, description, and deterministic fallback notes are presentation-only",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "MUST NOT be used to discover the change set.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use local git as the authoritative source for the exact candidate set:",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "A deterministic pre-agent step already preloaded the authoritative marker range",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "and history into this checkout.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not perform any network fetch in the agent",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "step.",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "`git log --format='%H%x09%s' --no-merges <from>..<to> -- extension/`",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Count the candidates produced by that command",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Consider and classify **every** candidate",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "You may group related user-facing commits into one final note",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "MUST NOT stop after a fixed number",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "report the exact candidate count from Step 4",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "how many candidates were included in the final notes and how many were excluded",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "the included/excluded totals (or equivalent auditable classification totals)",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "The local git range still cannot be enumerated after the pre-agent history",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "preload, or required enrichment searches fail outright",
            prompt,
            StringComparison.Ordinal);
        Assert.False(
            prompt.Contains("Use the compare API to get the commits between the validated SHAs:", StringComparison.Ordinal),
            "The prompt must not treat API pagination as the authoritative candidate set.");
        Assert.False(
            prompt.Contains("If the checkout is shallow or either SHA is missing locally, fetch the exact", StringComparison.Ordinal),
            "The prompt must not ask the agent to fetch history after credentials are removed.");
        Assert.False(
            prompt.Contains("after explicit fetch", StringComparison.Ordinal),
            "The prompt must not describe failure handling in terms of an agent-side fetch that is no longer allowed.");
        Assert.Contains("{{#runtime-import .github/workflows/extension-changelog.md}}", compiledWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledWorkflowPreloadsAuthoritativeRangeBeforeCleaningCredentials()
    {
        var workflow = File.ReadAllText(s_changelogWorkflowLockPath);
        var preloadStep = GetSection(
            workflow,
            "^      - name: Preload authoritative marker range for local changelog enumeration",
            "^      - name: Download container images");

        var configureGitCredentialsIndex = FindRequiredText(workflow, "Configure Git credentials");
        var checkoutPrBranchIndex = FindRequiredText(workflow, "Checkout PR branch");
        var preloadIndex = FindRequiredText(workflow, "Preload authoritative marker range for local changelog enumeration");
        var cleanCredentialsIndex = FindRequiredText(workflow, "Clean credentials");

        Assert.True(
            preloadIndex > configureGitCredentialsIndex,
            "The authoritative range preload must run after Git credentials are configured.");
        Assert.True(
            preloadIndex > checkoutPrBranchIndex,
            "The authoritative range preload must run after the PR branch checkout.");
        Assert.True(
            preloadIndex < cleanCredentialsIndex,
            "The authoritative range preload must finish before gh-aw removes Git credentials.");
        Assert.Contains("extension/CHANGELOG.md", preloadStep, StringComparison.Ordinal);
        Assert.Contains("aspire-ext-changelog", preloadStep, StringComparison.Ordinal);
        Assert.Contains("git fetch --no-tags", preloadStep, StringComparison.Ordinal);
        Assert.Contains("git log --format='%H%x09%s' --no-merges", preloadStep, StringComparison.Ordinal);
        Assert.Contains("${FROM_SHA}..${TO_SHA}", preloadStep, StringComparison.Ordinal);
        Assert.Contains("-- extension/ >/dev/null 2>&1", preloadStep, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task CompiledWorkflowPreloadScriptHasValidBashSyntax()
    {
        var script = GetCompiledWorkflowRunScript("Preload authoritative marker range for local changelog enumeration");
        var result = await RunBashSyntaxCheckAsync(script);

        Assert.True(
            result.ExitCode == 0,
            $"Expected compiled preload script to pass 'bash -n'.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task DeterministicFallbackIncludesEveryAcceptedCommitOnWindows()
        => DeterministicFallbackIncludesEveryAcceptedCommit("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task DeterministicFallbackIncludesEveryAcceptedCommitOnUnix()
        => DeterministicFallbackIncludesEveryAcceptedCommit("python3");

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task DeterministicFallbackAllowsRenderedOutputLargerThanEightThousandBytesOnWindows()
        => DeterministicFallbackAllowsRenderedOutputLargerThanEightThousandBytes("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task DeterministicFallbackAllowsRenderedOutputLargerThanEightThousandBytesOnUnix()
        => DeterministicFallbackAllowsRenderedOutputLargerThanEightThousandBytes("python3");

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task DeterministicFallbackSanitizesSplitlinesControlCharactersWithoutSplittingCommitsOnWindows()
        => DeterministicFallbackSanitizesSplitlinesControlCharactersWithoutSplittingCommits("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task DeterministicFallbackSanitizesSplitlinesControlCharactersWithoutSplittingCommitsOnUnix()
        => DeterministicFallbackSanitizesSplitlinesControlCharactersWithoutSplittingCommits("python3");

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task DeterministicFallbackStripsPrSuffixFromCrLfInputOnWindows()
        => DeterministicFallbackStripsPrSuffixFromCrLfInput("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task DeterministicFallbackStripsPrSuffixFromCrLfInputOnUnix()
        => DeterministicFallbackStripsPrSuffixFromCrLfInput("python3");

    private async Task DeterministicFallbackIncludesEveryAcceptedCommit(string pythonExecutable)
    {
        var output = await GenerateDeterministicReleaseNotesAsync(
            pythonExecutable,
            [
                "1111111\tfeat: Add Aspire dashboard tree view (#100)",
                "2222222\tfix(settings): Preserve <b>launch</b> profile labels (#101)",
                "3333333\tdocs: Explain terminal attach behavior (#102)",
                "4444444\tchore(extension): Surface package warnings\a in output (#103)",
                "5555555\trefactor: Improve project detection after restore (#104)",
                "6666666\tperf: Speed up solution scanning (#105)",
                "7777777\tfeat(tree): Show Azure resources in explorer (#106)",
                "8888888\tfix: Respect multi-root workspaces (#107)",
                "9999999\tfeat: Add walkthrough links (#108)",
                "aaaaaaa\tfix(debug): Keep env vars when reloading (#109)",
                "bbbbbbb\tfeat(commands): Support remove service action (#110)",
                "ccccccc\tchore: Handle workspace rename notifications (#111)",
                "ddddddd\tRelease 1.10.1",
                "eeeeeee\tBump package-lock.json",
                "fffffff\tUpdate yarn.lock"
            ]);

        var bulletLines = output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                "- Add Aspire dashboard tree view",
                "- Preserve launch profile labels",
                "- Explain terminal attach behavior",
                "- Surface package warnings in output",
                "- Improve project detection after restore",
                "- Speed up solution scanning",
                "- Show Azure resources in explorer",
                "- Respect multi-root workspaces",
                "- Add walkthrough links",
                "- Keep env vars when reloading",
                "- Support remove service action",
                "- Handle workspace rename notifications"
            ],
            bulletLines);
    }

    private async Task DeterministicFallbackAllowsRenderedOutputLargerThanEightThousandBytes(string pythonExecutable)
    {
        var commitLines = Enumerable.Range(1, 12)
            .Select(index =>
            {
                var message = $"candidate note {index:D2} " + new string((char)('a' + (index % 26)), 720);
                return $"{index:x7}\tfeat: {message}";
            })
            .ToArray();

        var output = await GenerateDeterministicReleaseNotesAsync(pythonExecutable, commitLines);
        var lastMessage = $"candidate note 12 {new string('m', 720)}";

        Assert.True(
            Encoding.UTF8.GetByteCount(output) > 8000,
            $"Expected rendered fallback to exceed 8000 bytes, but it was {Encoding.UTF8.GetByteCount(output)} bytes.");
        Assert.Contains($"- {lastMessage}", output, StringComparison.Ordinal);
    }

    private async Task DeterministicFallbackSanitizesSplitlinesControlCharactersWithoutSplittingCommits(string pythonExecutable)
    {
        var output = await GenerateDeterministicReleaseNotesAsync(
            pythonExecutable,
            [
                "1111111\tfeat: Alpha\rBeta (#100)",
                "2222222\tfix: Gamma\vDelta (#101)"
            ]);

        var bulletLines = output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                "- AlphaBeta",
                "- GammaDelta"
            ],
            bulletLines);
    }

    private async Task DeterministicFallbackStripsPrSuffixFromCrLfInput(string pythonExecutable)
    {
        var output = await GenerateDeterministicReleaseNotesAsync(
            pythonExecutable,
            ["1111111\tfeat: Alpha (#100)"],
            lineEnding: "\r\n");

        var bulletLines = output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(["- Alpha"], bulletLines);
    }

    private async Task<string> GenerateDeterministicReleaseNotesAsync(string pythonExecutable, IEnumerable<string> commitLines, string? lineEnding = null)
    {
        Assert.True(File.Exists(s_releaseNotesGeneratorPath), $"Expected release notes generator at '{s_releaseNotesGeneratorPath}'.");

        var tempDirectory = Directory.CreateTempSubdirectory("extension-release-notes");
        try
        {
            var commitsPath = Path.Combine(tempDirectory.FullName, "commits.txt");
            var outputPath = Path.Combine(tempDirectory.FullName, "release_notes.md");

            await File.WriteAllTextAsync(
                commitsPath,
                string.Join(lineEnding ?? Environment.NewLine, commitLines) + (lineEnding ?? Environment.NewLine));

            var startInfo = new ProcessStartInfo(pythonExecutable)
            {
                WorkingDirectory = RepoRoot.Path,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(s_releaseNotesGeneratorPath);
            startInfo.ArgumentList.Add(commitsPath);
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {pythonExecutable}.");

            // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

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
                $"{pythonExecutable} exited with code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            return await File.ReadAllTextAsync(outputPath);
        }
        finally
        {
            Directory.Delete(tempDirectory.FullName, recursive: true);
        }
    }

    private static string GetSection(string text, string startPattern, string endPattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            $"(?ms){startPattern}\\r?\\n.*?(?={endPattern})",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not find workflow section starting with '{startPattern}'.");
        return match.Value;
    }

    private static int FindRequiredText(string contents, string text)
    {
        var index = contents.IndexOf(text, StringComparison.Ordinal);

        Assert.True(index >= 0, $"Expected to find '{text}'.");

        return index;
    }

    private static string GetCompiledWorkflowRunScript(string stepName)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(s_changelogWorkflowLockPath));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];

        foreach (var jobEntry in jobs.Children)
        {
            if (jobEntry.Value is not YamlMappingNode job
                || !job.Children.TryGetValue(new YamlScalarNode("steps"), out var stepsNode)
                || stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                if (Scalar(step, "name") == stepName)
                {
                    var run = Scalar(step, "run");
                    Assert.False(string.IsNullOrEmpty(run), $"Expected step '{stepName}' to define a run script.");
                    return run!;
                }
            }
        }

        Assert.Fail($"Could not find workflow step '{stepName}'.");
        return null!;
    }

    private async Task<CommandResult> RunBashSyntaxCheckAsync(string script)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.WorkingDirectory = RepoRoot.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();

        // Read both streams concurrently to avoid deadlock when bash emits parser diagnostics.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        var output = await stdoutTask + await stderrTask;
        testOutput.WriteLine(output);

        return new CommandResult(process.ExitCode, output);
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private sealed record CommandResult(int ExitCode, string Output);
}
