// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Pins lifecycle facts, completion aliases, exact report bytes and early-failure finalization.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    public async Task EarlyClientFactoryFailureRetainsUnknownFactsAndOriginalError()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var output = Path.Combine(_workspace.Path, "workflow-output");
        var error = new ValidationException("client factory failed");

        var exitCode = await EvidencePreparation.CollectAsync(
            _workspace.Path,
            "microsoft/aspire",
            invocation,
            "123",
            runAttemptText: null,
            output,
            () => throw error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(invocation.Evidence.Outcome));
        Assert.Null(outcome.HasRelevantFailures);
        Assert.Null(outcome.RunId);
        Assert.Same(error, invocation.Capture.Failure);
        Assert.Equal("client factory failed", Assert.Single(outcome.Problems).Message);
        Assert.True(File.Exists(Path.Combine(invocation.RecoveryDirectory("collection"), "manifest.json")));
        Assert.Equal("analysis_ready=false\ncollection_status=failed\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task InvalidDecodedReplayRetainsRunFactsWithoutAuthorizingEvidence()
    {
        var input = new EvidencePaths(Path.Combine(_workspace.Path, "input"));
        Directory.CreateDirectory(input.RootDirectory);
        var bundle = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main) with { Jobs = [] };
        await File.WriteAllTextAsync(input.Bundle, TransientJson.Write(bundle));
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);

        await Assert.ThrowsAsync<ValidationException>(() => EvidencePreparation.ReplayAsync(input, invocation, CancellationToken.None));

        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(invocation.Replay.Outcome));
        Assert.True(outcome.HasRelevantFailures);
        Assert.Equal(bundle.Run.Id, outcome.RunId);
        Assert.Equal(bundle.Run.Attempt, outcome.RunAttempt);
        Assert.Equal(bundle.Run.HeadSha, outcome.HeadSha);
        Assert.Equal(bundle.Run.Url, outcome.RunUrl);
        Assert.Equal("failed", outcome.Status);
        Assert.False(File.Exists(invocation.Replay.Bundle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamedFailureFlagKeepsCompleteOutcomeWireShape(bool? relevantFailures)
    {
        var outcome = new AnalysisProgress { HasRelevantFailures = relevantFailures }.CreateOutcome("ready");
        var json = JsonNode.Parse(TransientJson.Write(outcome))!.AsObject();
        Assert.Equal(
            ["schema_version", "status", "has_work", "run_id", "run_attempt", "head_sha", "run_url", "run_scope", "subject_pr_number", "problems"],
            json.Select(property => property.Key));
        Assert.Equal(relevantFailures, json["has_work"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("true", "success", "ready", "success", 0)]
    [InlineData("true", "skipped", "ready", "success", 1)]
    [InlineData("true", "skipped", "degraded", "success", 0)]
    [InlineData("true", "failure", "degraded", "success", 0)]
    [InlineData("true", "skipped", "degraded", "failure", 1)]
    [InlineData("true", "success", "failed", "failure", 0)]
    [InlineData("true", "success", "failed", "cancelled", 0)]
    [InlineData("false", "failure", "failed", "failure", 0)]
    [InlineData(null, "failure", "failed", "failure", 0)]
    [InlineData("", "failure", "failed", "failure", 0)]
    [InlineData("invalid", "success", "degraded", "success", 1)]
    public async Task CompletionCommandAndLegacyAliasPreserveResultPolicy(
        string? relevantFailures,
        string publication,
        string status,
        string collection,
        int expected)
    {
        foreach (var command in new[] { "check-completion", "require-publication" })
        {
            var result = await RunValidatorCommandAsync(
                ["workflow", command],
                new Dictionary<string, string>
                {
                    ["HAS_WORK"] = relevantFailures ?? "",
                    ["PUBLICATION_RESULT"] = publication,
                    ["COLLECTION_STATUS"] = status,
                    ["COLLECTION_RESULT"] = collection,
                });
            Assert.Equal(expected, result.ExitCode);
            if (expected == 1 && relevantFailures == "true")
            {
                Assert.Contains($"collection status: {status}", result.Output, StringComparison.Ordinal);
                Assert.Contains($"collection result: {collection}", result.Output, StringComparison.Ordinal);
                Assert.Contains($"publication result: {publication}", result.Output, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("check-completion")]
    [InlineData("require-publication")]
    public async Task CompletionHelpDoesNotAcquireCredentials(string command)
    {
        var result = await RunValidatorCommandAsync(["workflow", command, "--help"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.Path));
    }

    [Theory]
    [InlineData("populated")]
    [InlineData("empty")]
    [InlineData("absent")]
    [RequiresTools(["bash", "git"])]
    public async Task LimitedReportUsesExactUtf8LayoutAndAppendsWithoutRewritingSummary(string summaryKind)
    {
        var summary = Path.Combine(_workspace.Path, "summary.md");
        var prefix = new byte[] { 0xef, 0xbb, 0xbf, 0x41, 0x0d, 0x0a };
        await File.WriteAllBytesAsync(summary, prefix);
        var environment = new Dictionary<string, string>();
        if (summaryKind != "absent")
        {
            environment["GITHUB_STEP_SUMMARY"] = summaryKind == "empty" ? "" : summary;
        }
        var result = await RunCollectionAsync("runner-missing-diagnostics", environment, useWorkflowAdapter: true);
        Assert.True(result.ExitCode == 0, result.Output);
        var expected = """
            # Limited CI analysis

            [Source CI run, attempt 2](https://github.com/microsoft/aspire/actions/runs/123/attempts/2)

            Structured test results are unavailable. This is a limited-evidence outcome, not a complete diagnosis.

            - Captured failed jobs: 1
            - Captured structured test failures: 0
            - Confirmed runner-loss jobs: 0
            - Artifact acquisition/selection problems: 1
            - Test-result parsing problems: 0

            No causal history, issues, or PR analysis comments were published. Reruns are not authorized.

            Inspect the `ci-failure-evidence`, collection recovery, and collection diagnostics artifacts for source job annotations and acquisition details.
            """.ReplaceLineEndings("\n") + "\n";
        var bytes = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(EvidenceDirectory, "limited-analysis.md")));
        Assert.Equal(
            summaryKind == "populated" ? prefix.Concat(bytes).ToArray() : prefix,
            await File.ReadAllBytesAsync(summary));
    }
}
