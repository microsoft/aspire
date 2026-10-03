// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class PrDocsCheckWorkflowTests(ITestOutputHelper testOutput)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PreparedPullRequestInputsAreMaterialized()
    {
        foreach (var workflowName in new[] { "pr-docs-check.md", "pr-docs-check.lock.yml" })
        {
            var root = LoadWorkflow(workflowName);
            var steps = workflowName.EndsWith(".md", StringComparison.Ordinal)
                ? Sequence(root, "pre-agent-steps").Children.Cast<YamlMappingNode>()
                : Steps(Mapping(Mapping(root, "jobs"), "agent"));
            var prepare = Assert.Single(
                steps,
                step => ScalarOrNull(step, "name") == "Compute user-facing signals and PR context");
            Assert.Contains(
                "mv \"${FILES_JSON}\" .pr-docs-check/files.json",
                Scalar(prepare, "run"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SafeOutputTargetResolutionPrecedesPatchApplication()
    {
        var sourceRoot = LoadWorkflow("pr-docs-check.md");
        var safeOutputs = Mapping(sourceRoot, "safe-outputs");
        var sourceSteps = Sequence(safeOutputs, "steps").Children.Cast<YamlMappingNode>().ToList();
        var checkout = Step(sourceSteps, "Check out safe-output target resolver");
        var resolve = Step(sourceSteps, "Resolve safe-output patch base from canonical agent output");
        Assert.True(sourceSteps.IndexOf(resolve) > sourceSteps.IndexOf(checkout));
        Assert.Equal(
            "contains(needs.agent.outputs.output_types, 'create_pull_request')",
            Scalar(checkout, "if"));
        Assert.Equal(Scalar(checkout, "if"), Scalar(resolve, "if"));
        Assert.Equal(
            "${{ steps.resolve-target.outputs.branch || 'main' }}",
            Scalar(Mapping(safeOutputs, "create-pull-request"), "base-branch"));

        var compiledRoot = LoadWorkflow("pr-docs-check.lock.yml");
        var compiledSteps = Steps(Mapping(Mapping(compiledRoot, "jobs"), "safe_outputs"));
        var download = Step(compiledSteps, "Download agent output artifact");
        var compiledResolve = Step(compiledSteps, "Resolve safe-output patch base from canonical agent output");
        var apply = Step(compiledSteps, "Process Safe Outputs");
        Assert.True(compiledSteps.IndexOf(compiledResolve) > compiledSteps.IndexOf(download));
        Assert.True(compiledSteps.IndexOf(apply) > compiledSteps.IndexOf(compiledResolve));
        Assert.Equal(
            "${{ github.event.pull_request.number || github.event.inputs.pr_number }}",
            Scalar(Mapping(compiledResolve, "env"), "EXPECTED_SOURCE_PR_NUMBER"));
        Assert.Contains("resolve_safe_output_target.py", Scalar(compiledResolve, "run"), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAndCompiledWorkflowGuardDraftedPrBase()
    {
        foreach (var workflowName in new[] { "pr-docs-check.md", "pr-docs-check.lock.yml" })
        {
            var steps = Steps(Mapping(Mapping(LoadWorkflow(workflowName), "jobs"), "validate-docs-outcome"));
            var token = Step(steps, "Mint aspire-bot token (microsoft/aspire.dev)");
            var resolve = Step(steps, "Resolve drafted PR base");
            var validate = Step(steps, "Require a conclusive documentation outcome");

            Assert.True(steps.IndexOf(resolve) > steps.IndexOf(token));
            Assert.True(steps.IndexOf(validate) > steps.IndexOf(resolve));
            Assert.Equal(
                "${{ steps.aspire-dev-token.outputs.token }}",
                Scalar(Mapping(resolve, "env"), "GH_TOKEN"));

            var validationRun = Scalar(validate, "run");
            Assert.Contains("validate_outcome.py", validationRun, StringComparison.Ordinal);
            Assert.Contains("--created-pr-base", validationRun, StringComparison.Ordinal);
            AssertShellVariablesAreBound(
                validate,
                ["CREATED_PR_BASE", "CREATED_PR_URL", "EXPECTED_SOURCE_PR_NUMBER"]);
        }
    }

    [Theory]
    [InlineData("pr-docs-check.md", "https://github.com/microsoft/aspire.dev/pull/1531", "release/13.6", true)]
    [InlineData("pr-docs-check.lock.yml", "https://github.com/microsoft/aspire.dev/pull/1531", "release/13.6", true)]
    [InlineData("pr-docs-check.md", "https://github.com/external/aspire.dev/pull/1531", "main", false)]
    [InlineData("pr-docs-check.lock.yml", "https://github.com/external/aspire.dev/pull/1531", "main", false)]
    [InlineData("pr-docs-check.md", "https://github.com/microsoft/aspire.dev/pull/1531", "feature", false)]
    [InlineData("pr-docs-check.lock.yml", "https://github.com/microsoft/aspire.dev/pull/1531", "feature", false)]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The workflow script runs on an Ubuntu runner.")]
    public async Task DraftedPrBaseResolutionValidatesUrlAndBase(
        string workflowName, string createdPrUrl, string actualBase, bool succeeds)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The workflow script runs on an Ubuntu runner.");
        }

        using var workspace = TemporaryWorkspace.Create(testOutput);
        var binDirectory = Path.Combine(workspace.Path, "bin");
        Directory.CreateDirectory(binDirectory);
        var ghPath = Path.Combine(binDirectory, "gh");
        await File.WriteAllTextAsync(ghPath, """
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> "${FAKE_GH_LOG}"
            printf '%s\n' "${FAKE_GH_BASE}"
            """);
        File.SetUnixFileMode(
            ghPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var outputPath = Path.Combine(workspace.Path, "github-output.txt");
        var logPath = Path.Combine(workspace.Path, "gh.log");
        var resolve = Step(
            Steps(Mapping(Mapping(LoadWorkflow(workflowName), "jobs"), "validate-docs-outcome")),
            "Resolve drafted PR base");

        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(Scalar(resolve, "run"));
        process.StartInfo.WorkingDirectory = workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["CREATED_PR_URL"] = createdPrUrl;
        process.StartInfo.Environment["FAKE_GH_BASE"] = actualBase;
        process.StartInfo.Environment["FAKE_GH_LOG"] = logPath;
        process.StartInfo.Environment["GITHUB_OUTPUT"] = outputPath;
        process.StartInfo.Environment["PATH"] =
            $"{binDirectory}{Path.PathSeparator}{process.StartInfo.Environment["PATH"]}";

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdoutTask + await stderrTask;

        Assert.Equal(succeeds ? 0 : 1, process.ExitCode);
        if (succeeds)
        {
            Assert.Equal($"base={actualBase}\n", (await File.ReadAllTextAsync(outputPath)).ReplaceLineEndings("\n"));
        }
        else
        {
            Assert.False(File.Exists(outputPath));
        }

        if (createdPrUrl.Contains("/microsoft/aspire.dev/", StringComparison.Ordinal))
        {
            var ghArguments = await File.ReadAllTextAsync(logPath);
            Assert.Contains("/repos/microsoft/aspire.dev/pulls/1531", ghArguments, StringComparison.Ordinal);
            Assert.Contains(".base.ref", ghArguments, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(File.Exists(logPath));
            Assert.Contains("Created PR URL is not a microsoft/aspire.dev pull request", output, StringComparison.Ordinal);
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

    [Fact]
    public void SourceAndCompiledWorkflowValidateBaseBeforeDraftedSideEffects()
    {
        foreach (var workflowName in new[] { "pr-docs-check.md", "pr-docs-check.lock.yml" })
        {
            var steps = NotifyJobSteps(workflowName);
            var aspireDevToken = Step(steps, "Mint aspire-bot token (microsoft/aspire.dev)");
            var resolve = Step(steps, "Resolve drafted PR base");
            var prepare = Step(steps, "Prepare trusted documentation outcome");
            var aspireToken = Step(steps, "Mint aspire-bot token (microsoft/aspire)");
            var comment = Step(steps, "Post status comment on source PR");
            var review = Step(steps, "Request SME review on draft PR");

            Assert.True(steps.IndexOf(resolve) > steps.IndexOf(aspireDevToken));
            Assert.True(steps.IndexOf(prepare) > steps.IndexOf(resolve));
            Assert.True(steps.IndexOf(comment) > steps.IndexOf(prepare));
            Assert.True(steps.IndexOf(comment) > steps.IndexOf(aspireToken));
            Assert.True(steps.IndexOf(review) > steps.IndexOf(prepare));
            Assert.Equal(
                "${{ steps.aspire-dev-token.outputs.token }}",
                Scalar(Mapping(resolve, "env"), "GH_TOKEN"));
            Assert.Equal(
                "${{ steps.drafted-pr-base.outputs.base }}",
                Scalar(Mapping(prepare, "env"), "CREATED_PR_BASE"));
            var prepareRun = Scalar(prepare, "run");
            Assert.Contains("validate_outcome.py", prepareRun, StringComparison.Ordinal);
            Assert.Contains("--created-pr-base", prepareRun, StringComparison.Ordinal);
            Assert.Contains("--raw-safe-outputs", prepareRun, StringComparison.Ordinal);
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

    private async Task<NotificationResult> RunNotificationScriptAsync(
        string workflowName, string renderKind, int? errorStatus = null, string? errorMessage = null)
    {
        using var workspace = TemporaryWorkspace.Create(testOutput);
        var comment = Step(NotifyJobSteps(workflowName), "Post status comment on source PR");
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
            new { script = Scalar(Mapping(comment, "with"), "script"), outcomeFile, errorStatus, errorMessage },
            s_jsonOptions));

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

    private static YamlMappingNode LoadWorkflow(string fileName)
    {
        var contents = ReadWorkflow(fileName);
        if (fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            const string delimiter = "---";
            var start = contents.IndexOf(delimiter, StringComparison.Ordinal);
            var end = contents.IndexOf($"\n{delimiter}", start + delimiter.Length, StringComparison.Ordinal);
            Assert.Equal(0, start);
            Assert.True(end > start);
            contents = contents[(start + delimiter.Length)..end];
        }

        using var reader = new StringReader(contents);
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static List<YamlMappingNode> NotifyJobSteps(string workflowName)
    {
        var root = LoadWorkflow(workflowName);
        var job = workflowName.EndsWith(".md", StringComparison.Ordinal)
            ? Mapping(Mapping(Mapping(root, "safe-outputs"), "jobs"), "notify-source-pr")
            : Mapping(Mapping(root, "jobs"), "notify_source_pr");
        return Steps(job);
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => Sequence(job, "steps").Children.Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Step(IReadOnlyList<YamlMappingNode> steps, string name)
        => Assert.Single(steps, step => ScalarOrNull(step, "name") == name);

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;

    private static string? ScalarOrNull(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;

    private static void AssertShellVariablesAreBound(YamlMappingNode step, string[] expectedVariables)
    {
        var run = Scalar(step, "run");
        var environment = Mapping(step, "env");
        var referencedVariables = Regex.Matches(
                run,
                "\\$\\{(?<name>[A-Z_][A-Z0-9_]*)\\}",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

        Assert.Equal(expectedVariables, referencedVariables);
        Assert.All(referencedVariables, variable => Assert.Contains(new YamlScalarNode(variable), environment.Children.Keys));
    }

    private sealed record NotificationResult(
        CommentAttempt[] Attempts, string[] Warnings, string Summary, int SummaryWrites, string? Error);

    private sealed record CommentAttempt(string Owner, string Repo, int IssueNumber, string Body);
}
