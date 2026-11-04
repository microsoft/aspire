// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks Actions output protocol, completion policy and limited-evidence finalization.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task UnavailableSourceResultsProduceLimitedReportInsteadOfRequestingAgentDiagnosis()
    {
        var summary = Path.Combine(_workspace.Path, "step-summary");
        var result = await RunCollectionAsync(
            "runner-missing-diagnostics",
            new Dictionary<string, string> { ["GITHUB_STEP_SUMMARY"] = summary },
            useWorkflowAdapter: true);

        Assert.Equal(0, result.ExitCode);
        var outputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains("analysis_ready=false", outputs);
        Assert.Contains("collection_status=degraded", outputs);
        Assert.Contains("has_work=true", outputs);
        var report = await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "limited-analysis.md"));
        Assert.Equal(report, await File.ReadAllTextAsync(summary));
        await Verifier.Verify(report, "md").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("degraded", "success", 0)]
    [InlineData("degraded", "failure", 1)]
    [InlineData("degraded", "cancelled", 1)]
    [InlineData("degraded", "skipped", 1)]
    [InlineData("ready", "success", 1)]
    [InlineData("failed", "success", 1)]
    public async Task LimitedReportSatisfiesGateOnlyWhenCollectionCompleted(string status, string collection, int exitCode)
    {
        var result = await RunValidatorCommandAsync(
            ["workflow", "require-publication"],
            new Dictionary<string, string>
            {
                ["HAS_WORK"] = "true",
                ["PUBLICATION_RESULT"] = "skipped",
                ["COLLECTION_STATUS"] = status,
                ["COLLECTION_RESULT"] = collection,
            });

        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task LimitedReportWriteFailureDoesNotBecomeSuccessfulCollection()
    {
        var summary = Directory.CreateDirectory(Path.Combine(_workspace.Path, "step-summary")).FullName;
        var result = await RunCollectionAsync(
            "runner-missing-diagnostics",
            new Dictionary<string, string> { ["GITHUB_STEP_SUMMARY"] = summary },
            useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "collection-outcome.json")));
        Assert.Equal("failed", outcome.Status);
    }

    [Fact]
    public async Task CSharpOutputWriterEmitsExactUtf8WithoutBomOrPlatformNewlines()
    {
        var path = Path.Combine(_workspace.Path, "workflow-output");
        var outcome = new AnalysisProgress
        {
            RunId = 9007199254740993, RunAttempt = 2, HeadSha = "sha-\u03BB",
            RunUrl = "https://github.com/microsoft/aspire/actions/runs/9007199254740993",
            RunScope = RunScope.PullRequest, SubjectPrNumber = 9007199254740995, HasRelevantFailures = true,
        }.CreateOutcome("ready");

        await GitHubActionsIntegration.WriteOutputsAsync(path, outcome, CancellationToken.None);

        var expected = "run_id=9007199254740993\nrun_attempt=2\nhead_sha=sha-\u03BB\n" +
            "run_url=https://github.com/microsoft/aspire/actions/runs/9007199254740993\n" +
            "run_scope=pull-request\npr_numbers=9007199254740995\nhas_work=true\nanalysis_ready=true\ncollection_status=ready\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CSharpPreparationCancellationAfterCollectionFailsWithoutOutput()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var paths = invocation.Evidence;
        Directory.CreateDirectory(paths.RootDirectory);
        var progress = invocation.Progress;
        progress.HasRelevantFailures = true;
        progress.RunId = 123;
        progress.RunAttempt = 1;
        var outcome = progress.CreateOutcome("ready");
        await File.WriteAllTextAsync(paths.Outcome, TransientJson.Write(outcome));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Combine(_workspace.Path, "workflow-output");

        var exitCode = await EvidencePreparation.CompleteCollectionAsync(invocation, outcome, output, cancellation.Token);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output));
        var failed = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(paths.Outcome));
        Assert.Equal("failed", failed.Status);
        Assert.True(failed.HasRelevantFailures);
        Assert.Contains(failed.Problems, problem => problem.Stage == "workflow-output");
        var recovery = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(
            invocation.RecoveryDirectory("collection"),
            "collection-outcome.json")));
        Assert.Equal("failed", recovery.Status);
        Assert.True(recovery.HasRelevantFailures);
    }

    [Fact]
    public async Task CSharpOutputWriterValidatesWholeBatchBeforeAppending()
    {
        var output = Path.Combine(_workspace.Path, "workflow-output");
        await File.WriteAllTextAsync(output, "previous=value\n");
        var outcome = new AnalysisProgress
        {
            HasRelevantFailures = true, RunId = 123, RunAttempt = 1, HeadSha = "valid",
            RunUrl = "https://github.com/microsoft/aspire/actions/runs/123\ninjected=true",
        }.CreateOutcome("failed");

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            GitHubActionsIntegration.WriteOutputsAsync(output, outcome, CancellationToken.None));

        Assert.Contains("run_url", error.Message, StringComparison.Ordinal);
        Assert.Equal("previous=value\n", await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("true", "success", 0)]
    [InlineData("true", "failure", 1)]
    [InlineData("true", "skipped", 1)]
    [InlineData("true", "cancelled", 1)]
    [InlineData("true", "", 1)]
    [InlineData("false", "skipped", 0)]
    [InlineData("", "failure", 0)]
    [InlineData("invalid", "success", 1)]
    public async Task CSharpPublicationGateRequiresSuccessfulPublication(string hasWork, string publication, int exitCode)
    {
        var result = await RunValidatorCommandAsync(
            ["workflow", "require-publication"],
            new Dictionary<string, string> { ["HAS_WORK"] = hasWork, ["PUBLICATION_RESULT"] = publication });

        Assert.Equal(exitCode, result.ExitCode);
        if (hasWork == "true" && exitCode != 0)
        {
            Assert.Contains("did not reach an accepted terminal outcome", result.Output, StringComparison.Ordinal);
        }
        if (hasWork == "invalid")
        {
            Assert.Contains("HAS_WORK", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CollectionRejectsValuelessOutputBeforeAcquisition()
    {
        var result = await RunValidatorCommandAsync(
            ["collect", "--repo", "microsoft/aspire", "--run-id", "123", "--working-dir", _workspace.Path, "--github-output"],
            new Dictionary<string, string>
            {
                ["GITHUB_OUTPUT"] = "",
                ["EVENT_NAME"] = "workflow_run",
                ["WORKFLOW_RUN_ID"] = "123",
                ["WORKFLOW_RUN_ATTEMPT"] = "1",
            });

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Required argument missing for option: '--github-output'", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.Path));
    }

    [Fact]
    public async Task CollectionRequiresExplicitRunDespiteAmbientWorkflowInputs()
    {
        var result = await RunValidatorCommandAsync(
            ["collect", "--repo", "microsoft/aspire", "--working-dir", _workspace.Path],
            new Dictionary<string, string>
            {
                ["GITHUB_OUTPUT"] = Path.Combine(_workspace.Path, "workflow-output"),
                ["EVENT_NAME"] = "push",
                ["WORKFLOW_RUN_ID"] = "123",
                ["WORKFLOW_RUN_ATTEMPT"] = "1",
            });

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Option '--run-id' is required", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "workflow-output")));
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"items":[{"type":"publish_data"}]}""")]
    public async Task CSharpRerunReportsNoRequestsWithoutReadingProposal(string json)
    {
        var output = Path.Combine(_workspace.Path, "agent-output.json");
        await File.WriteAllTextAsync(output, json);

        var result = await RunValidatorCommandAsync(
            ["workflow", "rerun", "--repo", "microsoft/aspire", "--analysis-dir", _workspace.Path, "--working-dir", _workspace.Path],
            new Dictionary<string, string> { ["GH_AW_AGENT_OUTPUT"] = output, ["ENABLE_RERUN"] = "true" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No rerun_failed_jobs", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"items":null}""")]
    [InlineData("""{"items":[],"it\u0065ms":[]}""")]
    [InlineData("""{"items":[null]}""")]
    [InlineData("""{"items":[{}]}""")]
    [InlineData("""{"items":[{"type":1}]}""")]
    public async Task CSharpRerunRejectsMalformedOutputEnvelope(string json)
    {
        var output = Path.Combine(_workspace.Path, "agent-output.json");
        await File.WriteAllTextAsync(output, json);

        var result = await RunValidatorCommandAsync(
            ["workflow", "rerun", "--repo", "microsoft/aspire", "--analysis-dir", _workspace.Path, "--working-dir", _workspace.Path],
            new Dictionary<string, string> { ["GH_AW_AGENT_OUTPUT"] = output, ["ENABLE_RERUN"] = "true" });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("::error::", result.Output, StringComparison.Ordinal);
    }
}
