// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class PrDocsCheckWorkflowTests(ITestOutputHelper testOutput)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PromptUsesPreparedInputsAndQueuesNotificationBeforeTerminalOutput()
    {
        var workflow = ReadWorkflow("pr-docs-check.md");
        var skillStep = GetSection(workflow, "^## Step 7: [^\r\n]*", "^## Step 8:");
        var notificationStep = GetSection(workflow, "^## Step 10: [^\r\n]*", "^## Step 11:");
        var finalStep = GetSection(workflow, "^## Step 11: [^\r\n]*", "\\z");

        Assert.Contains(".pr-docs-check/doc-writer/SKILL.md", skillStep, StringComparison.Ordinal);
        Assert.Contains("relevant relative references", skillStep, StringComparison.Ordinal);
        Assert.Contains("mv \"${FILES_JSON}\" .pr-docs-check/files.json", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "mv \\\"${FILES_JSON}\\\" .pr-docs-check/files.json",
            ReadWorkflow("pr-docs-check.lock.yml"),
            StringComparison.Ordinal);
        Assert.Contains("start finalizing by invocation 35", workflow, StringComparison.Ordinal);
        Assert.Contains("emit `notify_source_pr` first", notificationStep, StringComparison.Ordinal);
        Assert.Contains("**Stop after `create_pull_request`, whether it succeeds or fails.**", finalStep, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceWorkflowResolvesCanonicalTargetIntoSafeOutputs()
    {
        var workflow = ReadWorkflow("pr-docs-check.md");
        var safeOutputs = GetSection(workflow, "^safe-outputs:", "^pre-agent-steps:");
        var customSteps = GetSection(safeOutputs, "^  steps:", "^  create-pull-request:");

        Assert.Contains("Resolve safe-output patch base from canonical agent output", customSteps, StringComparison.Ordinal);
        Assert.Contains(
            "if: contains(needs.agent.outputs.output_types, 'create_pull_request')",
            customSteps,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                customSteps,
                "if: contains\\(needs\\.agent\\.outputs\\.output_types, 'create_pull_request'\\)",
                RegexOptions.CultureInvariant).Count);
        Assert.Contains("/tmp/gh-aw/agent_output.json", customSteps, StringComparison.Ordinal);
        Assert.Contains("/tmp/gh-aw/safeoutputs.jsonl", customSteps, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf -- _resolver' EXIT", customSteps, StringComparison.Ordinal);
        Assert.Contains(
            "resolve_safe_output_target.py",
            customSteps,
            StringComparison.Ordinal);
        Assert.Contains(
            "EXPECTED_SOURCE_PR_NUMBER: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}",
            customSteps,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(customSteps, "actions/checkout@", RegexOptions.CultureInvariant).Cast<Match>());
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
        Assert.Equal(
            2,
            Regex.Matches(
                safeOutputs,
                "(?m)^        if: contains\\(needs\\.agent\\.outputs\\.output_types, 'create_pull_request'\\)$",
                RegexOptions.CultureInvariant).Count);
        Assert.Contains("/tmp/gh-aw/agent_output.json", safeOutputs, StringComparison.Ordinal);
        Assert.Contains("/tmp/gh-aw/safeoutputs.jsonl", safeOutputs, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf -- _resolver' EXIT", safeOutputs, StringComparison.Ordinal);
        Assert.Contains("permission-contents: write", safeOutputs, StringComparison.Ordinal);
        Assert.Contains("resolve_safe_output_target.py", safeOutputs, StringComparison.Ordinal);
        Assert.Contains(
            "EXPECTED_SOURCE_PR_NUMBER: ${{ github.event.pull_request.number || github.event.inputs.pr_number }}",
            safeOutputs,
            StringComparison.Ordinal);
        Assert.Contains(
            "\\\"base_branch\\\":\\\"${{ steps.resolve-target.outputs.branch || 'main' }}\\\"",
            safeOutputs,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            Regex.Matches(safeOutputs, "uses: actions/checkout@", RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void OutcomeValidatorUsesSharedCanonicalTargetResolver()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepoRoot.Path, ".github", "workflows", "pr-docs-check", "validate_outcome.py"));

        Assert.Contains("resolve_target_branch(", validator, StringComparison.Ordinal);
        Assert.Contains("raw_safe_outputs", validator, StringComparison.Ordinal);
        Assert.Contains("require_target_branch(", validator, StringComparison.Ordinal);
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
            AssertCrossRepoLookupUsesAppToken(validationJob);
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

            var validationStep = GetSection(
                validationJob,
                "^      - name: Require a conclusive documentation outcome",
                "\\z");
            AssertShellVariablesAreBound(
                validationStep,
                ["CREATED_PR_BASE", "CREATED_PR_URL", "EXPECTED_SOURCE_PR_NUMBER"]);
        }
    }

    [Theory]
    [InlineData("pr-docs-check.md", "drafted")]
    [InlineData("pr-docs-check.lock.yml", "drafted")]
    [InlineData("pr-docs-check.md", "skipped")]
    [InlineData("pr-docs-check.lock.yml", "skipped")]
    [InlineData("pr-docs-check.md", "draft_failed")]
    [InlineData("pr-docs-check.lock.yml", "draft_failed")]
    [RequiresTools(["node"])]
    public async Task LockedSourcePrPreservesOutcomeInJobSummary(string workflowName, string renderKind)
    {
        var posted = await RunNotificationScriptAsync(workflowName, renderKind);
        var locked = await RunNotificationScriptAsync(
            workflowName, renderKind, 403, "Unable to create comment because issue is locked.");

        Assert.Null(posted.Error);
        Assert.Null(locked.Error);
        var comment = Assert.Single(posted.Attempts);
        Assert.Equal(comment, Assert.Single(locked.Attempts));
        Assert.Equal("microsoft", comment.Owner);
        Assert.Equal("aspire", comment.Repo);
        Assert.Equal(20195, comment.IssueNumber);
        Assert.Equal(
            $"Source PR microsoft/aspire#20195 is locked; no comment was posted.\n\n{comment.Body}",
            locked.Summary);
        Assert.Equal(
            ["Source PR microsoft/aspire#20195 is locked; the documentation outcome is recorded in the job summary."],
            locked.Warnings);
        Assert.Equal(1, locked.SummaryWrites);
        Assert.Equal(0, posted.SummaryWrites);
        Assert.Empty(posted.Warnings);
        if (renderKind == "drafted")
        {
            Assert.Contains("[microsoft/aspire.dev#1531](https://github.com/microsoft/aspire.dev/pull/1531)", comment.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("pr-docs-check.md", 403, "Resource not accessible by integration")]
    [InlineData("pr-docs-check.lock.yml", 403, "Resource not accessible by integration")]
    [InlineData("pr-docs-check.md", 422, "Validation Failed")]
    [InlineData("pr-docs-check.lock.yml", 422, "Validation Failed")]
    [InlineData("pr-docs-check.md", 500, "Unable to create comment because issue is locked.")]
    [InlineData("pr-docs-check.lock.yml", 500, "Unable to create comment because issue is locked.")]
    [RequiresTools(["node"])]
    public async Task OtherCommentFailuresRemainFatal(string workflowName, int status, string message)
    {
        var result = await RunNotificationScriptAsync(workflowName, "drafted", status, message);

        Assert.Equal(message, result.Error);
        Assert.Single(result.Attempts);
        Assert.Equal(0, result.SummaryWrites);
        Assert.Empty(result.Warnings);
    }

    private async Task<NotificationResult> RunNotificationScriptAsync(
        string workflowName, string renderKind, int? errorStatus = null, string? errorMessage = null)
    {
        using var workspace = TemporaryWorkspace.Create(testOutput);
        var commentStep = GetSection(
            ReadWorkflow(workflowName),
            "^\\s*- name: Post status comment on source PR",
            "^\\s*- name: Request SME review on draft PR");
        var scriptMatch = Regex.Match(commentStep, @"(?ms)^ +script: \|\r?\n(?<script>.*)");
        Assert.True(scriptMatch.Success);
        var lines = scriptMatch.Groups["script"].Value.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .Min(line => line.Length - line.TrimStart().Length);
        var script = string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line));
        var outcomeFile = Path.Combine(workspace.Path, "outcome.json");
        await File.WriteAllTextAsync(outcomeFile, JsonSerializer.Serialize(new
        {
            allow_comment = true,
            source_pr_number = 20195,
            render_kind = renderKind,
            target_branch = "release/13.6",
            summary = "Document the new CLI flags.",
        }));
        var requestPath = Path.Combine(workspace.Path, "request.json");
        var resultPath = Path.Combine(workspace.Path, "result.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(
            new { script, outcomeFile, errorStatus, errorMessage }, s_jsonOptions));

        using var command = new NodeCommand(testOutput, "pr-docs-check-notification");
        command.WithWorkingDirectory(RepoRoot.Path).WithTimeout(TimeSpan.FromSeconds(30));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "pr-docs-check-notification.harness.js"),
            requestPath, resultPath);
        Assert.Equal(0, result.ExitCode);
        var response = JsonSerializer.Deserialize<NotificationResult>(
            await File.ReadAllTextAsync(resultPath), s_jsonOptions);
        Assert.NotNull(response);
        return response;
    }

    [Fact]
    public void SourceAndCompiledWorkflowValidateBaseBeforeDraftedSideEffects()
    {
        foreach (var workflowName in new[] { "pr-docs-check.md", "pr-docs-check.lock.yml" })
        {
            var workflow = ReadWorkflow(workflowName);
            var notifyJob = GetSection(
                workflow,
                workflowName.EndsWith(".md", StringComparison.Ordinal)
                    ? "^    notify-source-pr:"
                    : "^  notify_source_pr:",
                workflowName.EndsWith(".md", StringComparison.Ordinal)
                    ? "^# The agent that follows"
                    : "^  safe_outputs:");

            AssertCrossRepoLookupUsesAppToken(notifyJob);
            var mintIndex = notifyJob.IndexOf("Mint aspire-bot token (microsoft/aspire.dev)", StringComparison.Ordinal);
            var resolveIndex = notifyJob.IndexOf("Resolve drafted PR base", StringComparison.Ordinal);
            var prepareIndex = notifyJob.IndexOf("Prepare trusted documentation outcome", StringComparison.Ordinal);
            var commentIndex = notifyJob.IndexOf("Post status comment on source PR", StringComparison.Ordinal);
            var reviewIndex = notifyJob.IndexOf("Request SME review on draft PR", StringComparison.Ordinal);

            Assert.True(resolveIndex > mintIndex, "The drafted PR lookup must run after app-token minting.");
            Assert.True(prepareIndex > resolveIndex, "Side-effect validation must run after the actual base lookup.");
            Assert.True(commentIndex > prepareIndex, "Source comments must use the base-validated outcome.");
            Assert.True(reviewIndex > prepareIndex, "SME review requests must use the base-validated outcome.");
            Assert.Contains(
                "CREATED_PR_BASE: ${{ steps.drafted-pr-base.outputs.base }}",
                notifyJob,
                StringComparison.Ordinal);
            Assert.Contains("--created-pr-base \"${CREATED_PR_BASE}\"", notifyJob, StringComparison.Ordinal);
            Assert.Contains(
                "--raw-safe-outputs \"$(dirname \"${GH_AW_AGENT_OUTPUT}\")/safeoutputs.jsonl\"",
                notifyJob,
                StringComparison.Ordinal);
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

    private static void AssertShellVariablesAreBound(string step, string[] expectedVariables)
    {
        var run = Regex.Match(
            step,
            "(?ms)^        run: (?<value>.*?)(?=^        [a-z-]+:|\\z)",
            RegexOptions.CultureInvariant);
        var env = Regex.Match(
            step,
            "(?ms)^        env:\\r?\\n(?<value>.*?)(?=^        [a-z-]+:|\\z)",
            RegexOptions.CultureInvariant);
        Assert.True(run.Success, "Could not find the validation step's run command.");
        Assert.True(env.Success, "Could not find the validation step's environment.");

        var referencedVariables = Regex.Matches(
                run.Groups["value"].Value,
                "\\$\\{(?<name>[A-Z_][A-Z0-9_]*)\\}",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
        var boundVariables = Regex.Matches(
                env.Groups["value"].Value,
                "(?m)^          (?<name>[A-Z_][A-Z0-9_]*):",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedVariables, referencedVariables);
        Assert.All(referencedVariables, variable => Assert.Contains(variable, boundVariables));
    }

    private static void AssertCrossRepoLookupUsesAppToken(string job)
    {
        var mintIndex = job.IndexOf("Mint aspire-bot token (microsoft/aspire.dev)", StringComparison.Ordinal);
        var resolveIndex = job.IndexOf("Resolve drafted PR base", StringComparison.Ordinal);
        Assert.True(mintIndex >= 0, "The job must mint an aspire.dev app token.");
        Assert.True(resolveIndex > mintIndex, "The drafted PR lookup must run after app-token minting.");

        var resolveStep = GetSection(
            job,
            "^      (?:  )?- name: Resolve drafted PR base",
            "^      (?:  )?- name:");
        Assert.Contains(
            "GH_TOKEN: ${{ steps.aspire-dev-token.outputs.token }}",
            resolveStep,
            StringComparison.Ordinal);
        Assert.False(
            resolveStep.Contains("github.token", StringComparison.Ordinal),
            "The cross-repository lookup must not use the repository-scoped github.token.");
    }

    private sealed record NotificationResult(
        CommentAttempt[] Attempts, string[] Warnings, string Summary, int SummaryWrites, string? Error);

    private sealed record CommentAttempt(string Owner, string Repo, int IssueNumber, string Body);
}
