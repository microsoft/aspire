// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies bounded diagnostics, progress events and preserved publication receipts.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    private string DiagnosticSummary => Path.Combine(_invocationDirectory!, "diagnostics", "summary.json");

    [Fact]
    public async Task FailedLogAcquisitionSurvivesFocusingAndCreatesOptionalProblem()
    {
        var result = await RunCollectionAsync("failed-noisy-logs", useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("Unable to fetch complete logs for job 77", result.Output, StringComparison.Ordinal);
        var outcome = TransientJson.Read<AnalysisOutcome>(File.ReadAllText(Path.Combine(EvidenceDirectory, "collection-outcome.json")));
        Assert.Equal("ready", outcome.Status);
        Assert.Contains(
            outcome.Problems,
            problem => problem.Stage == "logs" &&
            problem.Message.Contains("GET /repos/microsoft/aspire/actions/jobs/77/logs: response body could not be read completely.", StringComparison.Ordinal));
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        var log = Assert.Single(summary["events"]!.AsArray(), item => item!["action"]!.GetValue<string>() == "job-log")!;
        Assert.Equal("partial", log["result"]!.GetValue<string>());
        Assert.Equal(300, log["detail"]!["source_lines"]!.GetValue<int>());
        Assert.Equal(150, log["detail"]!["retained_lines"]!.GetValue<int>());
        Assert.True(log["detail"]!["truncated"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(RecoveryDirectory("collection"), "manifest.json")));
        Assert.Contains("response body could not be read completely", File.ReadAllText(Path.Combine(EvidenceDirectory, "diagnostics", "job-77.log")), StringComparison.Ordinal);
        Assert.False(File.ReadAllText(DiagnosticSummary).Contains("collector-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoWorkSummaryExplainsJobSelectionWithoutAuthorizingPublication()
    {
        var result = await RunCollectionAsync("healthy", useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Equal("no-work", summary["status"]!.GetValue<string>());
        var jobs = Assert.Single(summary["events"]!.AsArray(), item => item!["action"]!.GetValue<string>() == "jobs")!;
        Assert.Equal("no-actionable-jobs", jobs["result"]!.GetValue<string>());
        Assert.Equal(0, jobs["detail"]!["actionable"]!.GetValue<int>());
        Assert.True(jobs["detail"]!["total"]!.GetValue<int>() > 0);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory, "evidence.json")));
        Assert.Contains(
            "diagnostics_dir=" + Path.GetDirectoryName(DiagnosticSummary),
            File.ReadAllLines(Path.Combine(_workspace.Path, "workflow-output")));
    }

    [Theory]
    [InlineData("ambiguous", "ambiguous")]
    [InlineData("fork-wrong-sha", "head-sha-mismatch")]
    public async Task SubjectPrResolutionReasonSurvivesReadyCollection(string scenario, string reason)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.True(result.ExitCode == 0, result.Output);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Contains(
            summary["events"]!.AsArray(),
            item => item!["action"]!.GetValue<string>() == "subject-pr" && item["result"]!.GetValue<string>() == reason);
    }

    [Fact]
    public async Task PartialPublicationRetainsConfirmedIssueUrlAfterDefaultScratchDisposal()
    {
        await LaterIssueFailureKeepsEarlierLocalLinkAndAllDurableOccurrences();
        File.Delete(Path.Combine(_workspace.Path, "first-issue-published"));
        var fakeGh = Assert.Single(Directory.GetFiles(_workspace.Path, "gh", SearchOption.AllDirectories));
        var result = await RunValidatorCommandAsync(
            ["publish", "--repo", "microsoft/aspire", "--evidence-dir", EvidenceDirectory,
            "--analysis-dir", Path.Combine(_workspace.Path, "ci-analysis-output")],
            new Dictionary<string, string> { ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}" });

        Assert.Equal(1, result.ExitCode);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Equal("failed", summary["status"]!.GetValue<string>());
        Assert.Contains(
            summary["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "issue-create" && receipt["result"]!.GetValue<string>() == "confirmed" &&
            receipt["detail"]!["url"]!.GetValue<string>() == "https://github.com/microsoft/aspire/issues/77");
        Assert.Contains(
            summary["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "issue-create" && receipt["result"]!.GetValue<string>() == "unknown");
        var recovery = JsonNode.Parse(File.ReadAllText(Path.Combine(RecoveryDirectory("publication"), "manifest.json")))!;
        Assert.Contains(
            recovery["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "issue-create" && receipt["detail"]!["url"]?.GetValue<string>() ==
                "https://github.com/microsoft/aspire/issues/77");
        Assert.False(Directory.Exists(Path.Combine(_invocationDirectory!, "scratch")));
    }

    [Fact]
    public async Task RecoveryFailureUpdatesFinalOutcomeAndDiagnosticSummary()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        Directory.CreateDirectory(Path.Combine(invocation.RootDirectory, "recovery"));
        await File.WriteAllTextAsync(invocation.RecoveryDirectory("collection"), "not a directory");

        await Assert.ThrowsAsync<ValidationException>(() => AnalyzeCiFailure.EvidencePreparation.CollectEvidenceAndPrepareAnalysisAsync(
            _workspace.Path,
            "microsoft/aspire",
            invocation,
            "invalid",
            "2",
            CreateGitHubClient(),
            CancellationToken.None));

        var outcome = TransientJson.Read<AnalysisOutcome>(File.ReadAllText(invocation.Evidence.Outcome));
        Assert.Contains(outcome.Problems, problem => problem.Stage == "recovery");
        Assert.All(outcome.Problems, problem => Assert.NotEqual("cleanup", problem.Stage));
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(invocation.RootDirectory, "diagnostics", "summary.json")))!;
        Assert.Equal("failed", summary["status"]!.GetValue<string>());
        Assert.Contains(summary["outcome"]!["problems"]!.AsArray(), problem => problem!["stage"]!.GetValue<string>() == "recovery");
    }

    [Fact]
    public async Task InvalidReplayRetainsExceptionRecoveryWithoutChangingInput()
    {
        var input = Directory.CreateDirectory(Path.Combine(_workspace.Path, "invalid-replay")).FullName;
        await File.WriteAllTextAsync(Path.Combine(input, "evidence.json"), "{}");
        var result = await RunValidatorCommandAsync(["replay", "--evidence-dir", input]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(input, "evidence.json")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RecoveryDirectory("replay"), "manifest.json")))!;
        Assert.NotNull(manifest["exception"]!["type"]);
        Assert.Equal("failed", JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedRetryPatternsWarnWithoutBlockingUsableEvidence()
    {
        Directory.CreateDirectory(Path.Combine(_workspace.Path, "eng"));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "eng", "test-retry-patterns.json"), "not JSON");

        var result = await RunCollectionAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("Unable to parse retry patterns", result.Output, StringComparison.Ordinal);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Contains(summary["outcome"]!["problems"]!.AsArray(), problem => problem!["stage"]!.GetValue<string>() == "retry-patterns");
    }

    [Fact]
    public async Task SuccessfulPublicationRetainsReceiptsAndStageDurations()
    {
        await WriteGroupedFixtureAsync();

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Equal("published", summary["status"]!.GetValue<string>());
        Assert.Contains(
            summary["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "history-push" && receipt["result"]!.GetValue<string>() == "confirmed" &&
            receipt["detail"]!["commit"]!.GetValue<string>().Length == 40);
        Assert.Contains(
            summary["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "pr-comment-create" && receipt["result"]!.GetValue<string>() == "confirmed" &&
            receipt["detail"]!["url"]!.GetValue<string>() == "https://github.com/microsoft/aspire/pull/42#issuecomment-123");
        Assert.All(
            summary["events"]!.AsArray().Where(item => item!["action"]!.GetValue<string>() == "stage"),
            item => Assert.True(item!["detail"]!["duration_ms"]!.GetValue<long>() >= 0));
    }

    [Fact]
    public async Task DiagnosticSummaryLimitsRemainValidJsonAndPreserveReceiptPriority()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        for (var i = 0; i < 1100; i++)
        {
            invocation.Progress.Diagnostics.Record("selection", "matched", new { name = new string('x', count: 5000), secret = "token=private-value" });
        }
        invocation.Progress.Diagnostics.Receipt("issue-create", "confirmed", new { url = "https://github.com/microsoft/aspire/issues/77" });

        await invocation.WriteDiagnosticsAsync("ready");

        var text = File.ReadAllText(Path.Combine(invocation.DiagnosticsDirectory, "summary.json"));
        var summary = JsonNode.Parse(text)!;
        Assert.Equal(1024, summary["events"]!.AsArray().Count);
        Assert.True(summary["omitted_entries"]!.GetValue<int>() >= 76);
        Assert.Equal("confirmed", Assert.Single(summary["receipts"]!.AsArray())!["result"]!.GetValue<string>());
        Assert.False(text.Contains("private-value", StringComparison.Ordinal));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= AnalysisDiagnostics.MaximumFileBytes);
    }

    [Fact]
    public async Task DiagnosticSummaryByteBudgetIncludesUnicodeProblems()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var message = string.Concat(Enumerable.Repeat("\U00010400", count: 500));
        for (var i = 0; i < 1024; i++)
        {
            invocation.Progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "logs", message);
        }
        invocation.Progress.Diagnostics.Receipt("issue-create", "confirmed", new { url = "https://github.com/microsoft/aspire/issues/77" });

        await invocation.WriteDiagnosticsAsync("ready");

        var text = File.ReadAllText(Path.Combine(invocation.DiagnosticsDirectory, "summary.json"));
        var summary = JsonNode.Parse(text)!;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= AnalysisDiagnostics.MaximumFileBytes);
        Assert.True(summary["omitted_problems"]!.GetValue<int>() > 0);
        Assert.Single(summary["receipts"]!.AsArray());
    }

    [Theory]
    [InlineData("not a comment URL")]
    [InlineData("https://github.com/other/repository/pull/42#issuecomment-123")]
    [InlineData("https://github.com/microsoft/aspire/pull/43#issuecomment-123")]
    public async Task UnrecognizedCommentCreationResponseIsRecordedAsUnknown(string response)
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "comment-response"), response);

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("canonical comment URL", result.Output, StringComparison.Ordinal);
        var summary = JsonNode.Parse(File.ReadAllText(DiagnosticSummary))!;
        Assert.Contains(
            summary["receipts"]!.AsArray(),
            receipt =>
            receipt!["action"]!.GetValue<string>() == "pr-comment-create" && receipt["result"]!.GetValue<string>() == "unknown");
    }

    [Fact]
    public async Task OutputFailurePreservesEarlierCollectionException()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        invocation.Capture.Failure = new ValidationException("original history failure");
        invocation.Progress.HasRelevantFailures = true;
        invocation.Progress.RecordProblem(AnalysisProblemCategory.Fatal, "history", "original history failure");
        var output = Directory.CreateDirectory(Path.Combine(_workspace.Path, "not-an-output-file")).FullName;

        var exitCode = await EvidencePreparation.CompleteCollectionAsync(
            invocation,
            invocation.Progress.CreateOutcome("failed"),
            output,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(invocation.DiagnosticsDirectory, "summary.json")))!;
        Assert.Equal("original history failure", summary["exception"]!["message"]!.GetValue<string>());
        Assert.Contains(summary["outcome"]!["problems"]!.AsArray(), problem => problem!["stage"]!.GetValue<string>() == "workflow-output");
    }
}
