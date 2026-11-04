// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises workflow-run admission, evidence acquisition and incomplete-result handling through the CLI.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task CompletedJobsCannotClaimRunnerLossFromMatchingWorkflowAnnotations()
    {
        var result = await RunCollectionAsync("runner-confirmed-loss-completed-missing-artifact", useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json")))!;
        var job = Assert.Single(evidence["jobs"]!.AsArray(), job => job!["id"]!.GetValue<string>() == "77")!;
        Assert.Null(job["runner_failure"]);
        Assert.Equal("unavailable", evidence["test_evidence"]!["state"]!.GetValue<string>());
        Assert.Contains("analysis_ready=false", await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output")));
    }

    [Theory]
    [InlineData("invalid-size")]
    [InlineData("ambiguous")]
    [InlineData("expired")]
    [InlineData("ambiguous-job")]
    [RequiresTools(["bash", "git"])]
    public async Task RunnerLossDoesNotExcuseAnExistingUnusableArtifact(string kind)
    {
        var result = await RunCollectionAsync("runner-confirmed-loss-" + kind, useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("analysis_ready=false", await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output")));
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json")))!;
        Assert.Equal("unavailable", evidence["test_evidence"]!["state"]!.GetValue<string>());
        Assert.Equal("Other.Failure", Assert.Single(evidence["test_evidence"]!["failures"]!.AsArray())!["name"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ConfirmedRunnerLossExplainsMissingResultsWithoutBlockingAnalysis()
    {
        var result = await RunCollectionAsync("runner-confirmed-loss", useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        var outputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains("analysis_ready=true", outputs);
        var json = await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json"));
        var bundle = AnalyzeCiFailure.TransientJson.Read<AnalyzeCiFailure.EvidenceBundle>(json);
        Assert.Equal("The hosted runner lost communication with the server.", JsonNode.Parse(json)!["jobs"]![0]!["runner_failure"]!.GetValue<string>());
        var accepted = AnalyzeCiFailure.AnalysisValidator.Validate(
            bundle,
            AnalyzeCiFailureFixtures.CreateInfrastructureProposal(bundle),
            "microsoft/aspire");
        Assert.Equal(AnalyzeCiFailure.AnalysisVerdict.TransientInfrastructure, accepted.Report.Verdict);
        Assert.Empty(accepted.Report.Tests);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task MalformedJobRowsFailCollectionInsteadOfReportingNoWork()
    {
        var result = await RunCollectionAsync("malformed-job-rows");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("expected an object", result.Output, StringComparison.Ordinal);
        Assert.Contains("actions/runs/123/attempts/2/jobs", result.Output, StringComparison.Ordinal);
        var outcome = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(EvidenceDirectory, "collection-outcome.json")))!;
        Assert.Equal("failed", outcome["status"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory, "evidence.json")));
    }
    [Theory]
    [InlineData("")]
    [InlineData("invalid/repository/name")]
    public void CollectorRejectsInvalidRepositoryDuringConstruction(string repository)
    {
        var paths = new AnalyzeCiFailure.EvidencePaths(EvidenceDirectory);
        using var scratch = new AnalyzeCiFailure.AnalysisScratchWorkspace();

        var error = Assert.Throws<AnalyzeCiFailure.ValidationException>(() =>
            new AnalyzeCiFailure.WorkflowRunEvidenceCollector(
                _workspace.Path,
                repository,
                scratch,
                new AnalyzeCiFailure.InvocationCapture(),
                new AnalyzeCiFailure.AspireCiWorkflowConventions(),
                commandEnvironment: null,
                CreateGitHubClient()));

        Assert.Equal("Invalid analysis repository", error.Message);
        Assert.False(Directory.Exists(paths.RootDirectory));
    }

    [Theory]
    [InlineData("valid", 0, true)]
    [InlineData("healthy", 0, false)]
    [InlineData("history-transport", 1, true)]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionWritesSummaryIncludingProgressBeforeFailure(string scenario, int exitCode, bool hasFailures)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.True(exitCode == result.ExitCode, result.Output);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(EvidenceDirectory, "collection-outcome.json")))!;
        Assert.Equal("123", summary["run_id"]!.GetValue<string>());
        Assert.Equal(2, summary["run_attempt"]!.GetValue<int>());
        Assert.Equal(hasFailures, summary["has_work"]!.GetValue<bool>());
        var pathOutputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "invocation-paths"));
        Assert.Contains("evidence_dir=" + EvidenceDirectory, pathOutputs);
        Assert.Contains(hasFailures ? "has_work=true" : "has_work=false", pathOutputs);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionUsesExplicitRunInputsInsteadOfAmbientWorkflowVariables()
    {
        var result = await RunCollectionAsync(environment: new Dictionary<string, string>
        {
            ["EVENT_NAME"] = "workflow_dispatch",
            ["MANUAL_RUN_ID"] = "invalid-ambient-run",
            ["WORKFLOW_RUN_ID"] = "invalid-ambient-run",
            ["WORKFLOW_RUN_ATTEMPT"] = "invalid-ambient-attempt",
        });

        Assert.True(result.ExitCode == 0, result.Output);
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("api repos/microsoft/aspire/actions/runs/123/attempts/2", calls);
    }

    [Theory]
    [InlineData("valid", 0, true)]
    [InlineData("healthy", 0, false)]
    [InlineData("history-transport", 1, true)]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionWritesOutputsAtCompletionIncludingFailures(string scenario, int exitCode, bool hasWork)
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "require-final-outputs"), "");

        var result = await RunCollectionAsync(scenario, useWorkflowAdapter: true);

        Assert.Equal(exitCode, result.ExitCode);
        var outputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains($"has_work={hasWork.ToString().ToLowerInvariant()}", outputs);
        Assert.Contains("run_id=123", outputs);
        if (scenario == "history-transport")
        {
            Assert.Contains("Unable to load CI failure history", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("runner-missing-diagnostics")]
    [RequiresTools(["bash", "git"])]
    public async Task WorkflowAdapterPreservesLatestAttemptAndDegradedEvidencePaths(string scenario)
    {
        var result = await RunCollectionAsync(scenario, useWorkflowAdapter: true);

        Assert.True(result.ExitCode == 0, result.Output);
        var outputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains("run_attempt=2", outputs);
        Assert.Contains("has_work=true", outputs);
        if (scenario == "manual")
        {
            Assert.Contains(
                "api repos/microsoft/aspire/actions/runs/123",
                await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")));
        }
        else
        {
            var evidence = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(EvidenceDirectory, "evidence.json")))!;
            Assert.Equal("unavailable", evidence["test_evidence"]!["state"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "git"])]
    public async Task WorkflowAdapterDoesNotInventNoWorkOnEarlyFailure(bool hasStaleResults)
    {
        if (hasStaleResults)
        {
            var directory = Directory.CreateDirectory(EvidenceDirectory).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(directory, "collection-outcome.json"),
                """{"run_id":"999","has_work":true}""");
            await File.WriteAllTextAsync(Path.Combine(directory, "analysis-summary.md"), "Previous run");
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), """{"test_evidence":{"state":"unavailable"}}""");
        }

        var result = await RunCollectionAsync("wrong-run", useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(EvidenceDirectory, "collection-outcome.json")))!;
        Assert.Null(summary["has_work"]);
        var pathOutputs = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains("evidence_dir=" + EvidenceDirectory, pathOutputs);
        Assert.All(pathOutputs, line => Assert.False(line.StartsWith("has_work=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WorkflowAdapterPreservesLargeIdentifiersFromCollectionSummary()
    {
        var outcome = new AnalyzeCiFailure.AnalysisOutcome
            {
                SchemaVersion = 1, Status = "ready", HeadSha = null, RunUrl = null, Problems = [],
                RunId = 9007199254740993,
                RunAttempt = 2,
                RunScope = AnalyzeCiFailure.RunScope.PullRequest,
                SubjectPrNumber = 9007199254740995,
                HasRelevantFailures = true,
            };
        await AnalyzeCiFailure.GitHubActionsIntegration.WriteOutputsAsync(
            Path.Combine(_workspace.Path, "workflow-output"),
            outcome,
            CancellationToken.None);

        await Verifier.Verify(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "workflow-output")), "txt")
            .UseDirectory("Snapshots");
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionRejectsEmptyExplicitOutputBeforeAnyGitHubCalls()
    {
        var result = await RunCollectionAsync(
            environment: new Dictionary<string, string>
            {
                ["GITHUB_ACTIONS"] = "true",
                ["GITHUB_OUTPUT"] = "",
            },
            useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--github-output must name a nonempty file path", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task LocalCollectionDoesNotRequireWorkflowOutputFile()
    {
        var result = await RunCollectionAsync(environment: new Dictionary<string, string>
        {
            ["GITHUB_ACTIONS"] = "",
            ["GITHUB_OUTPUT"] = "",
        });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(Path.Combine(EvidenceDirectory, "evidence.json")));
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "workflow-output")));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task WorkflowAdapterRejectsOutputDirectoryBeforeCollection()
    {
        var outputDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "output-is-directory")).FullName;

        var result = await RunCollectionAsync(
            environment: new Dictionary<string, string>
            {
                ["GITHUB_OUTPUT"] = outputDirectory,
            },
            useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--github-output must name a file", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("history-transport")]
    [RequiresTools(["bash", "git"])]
    public async Task WorkflowAdapterReportsOutputAppendFailureWithoutHidingCollectionFailure(string scenario)
    {
        var result = await RunCollectionAsync(
            scenario,
            new Dictionary<string, string>
            {
                ["GITHUB_OUTPUT"] = Path.Combine(_workspace.Path, "missing-parent", "workflow-output"),
            },
            useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("workflow-output", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("history-transport")]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionReportsSummaryWriteFailureWithoutLosingCollectionError(string scenario)
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "fail-outcome-write"), "");

        var result = await RunCollectionAsync(scenario);

        Assert.Equal(1, result.ExitCode);
        if (scenario == "history-transport")
        {
            Assert.Contains("Unable to write collection outcome", result.Output, StringComparison.Ordinal);
            Assert.Contains("Unable to load CI failure history", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("collection-outcome.json", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionRejectsNewlinesBeforeWritingAnyOutputs()
    {
        var result = await RunCollectionAsync("newline-output", useWorkflowAdapter: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Invalid workflow output value", result.Output, StringComparison.Ordinal);
        var paths = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "workflow-output"));
        Assert.Contains("evidence_dir=" + EvidenceDirectory, paths);
        Assert.All(paths, line => Assert.False(line.StartsWith("has_work=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("runner-failure")]
    [InlineData("runner-cancelled")]
    [InlineData("runner-fail-fast")]
    [InlineData("runner-cancelled-step")]
    [InlineData("runner-interrupted-with-failed-post")]
    [InlineData("runner-cancelled-test-with-failed-post")]
    [InlineData("runner-timed-out-test-with-failed-post")]
    [InlineData("runner-timed-out")]
    [InlineData("runner-missing-diagnostics")]
    [InlineData("runner-empty-artifact")]
    [InlineData("runner-download-failure")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareRetainsRunnerFailuresWithoutFailedSteps(string scenario)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.Equal(0, result.ExitCode);
        var data = EvidenceDirectory;
        var job = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/failed-jobs.json")))!.AsArray())!;
        Assert.Equal(77, job["id"]!.GetValue<int>());
        Assert.Equal(
            scenario switch
            {
                "runner-cancelled" or "runner-fail-fast" or "runner-cancelled-step" => "cancelled",
                "runner-timed-out" => "timed_out",
                _ => "failure",
            },
            job["conclusion"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!;
        Assert.Equal("unavailable", state["state"]!.GetValue<string>());
        Assert.Empty(state["failures"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(data, "analysis-summary.md")));
        var annotations = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/annotations-77.json")))!.AsArray();
        if (scenario == "runner-missing-diagnostics")
        {
            Assert.Empty(annotations);
            Assert.Contains("Unable to load job annotations", result.Output, StringComparison.Ordinal);
            Assert.Contains("Failed to fetch complete logs for job 77", await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/job-77.log")), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(
                scenario == "runner-fail-fast" ? "Cancelled after another matrix job failed" : "Runner lost communication",
                Assert.Single(annotations)!["message"]!.GetValue<string>());
        }
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareSkipsHealthyJobsAndDependencyFailureWrappers()
    {
        var result = await RunCollectionAsync("healthy");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory, "evidence.json")));
        Assert.False(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareIndependentlyExcludesFinalResultsAndDependencyFailureWrappers()
    {
        var result = await RunCollectionAsync("independent-wrappers");

        var jobs = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/failed-jobs.json")))!.AsArray();
        Assert.Equal([77], jobs.Select(job => job!["id"]!.GetValue<int>()));
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareRetainsNeverStartedCancelledBuildWithoutInventingTestEvidence()
    {
        var result = await RunCollectionAsync("cancelled-build");

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var job = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/failed-jobs.json")))!.AsArray())!;
        Assert.Equal("Build", job["name"]!.GetValue<string>());
        Assert.Equal("cancelled", job["conclusion"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
        Assert.Equal("not-applicable", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!["state"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!["failures"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareKeepsCompletedSuccessfulTestsDistinctFromInterruptedTestsWhenCleanupFails()
    {
        var result = await RunCollectionAsync("runner-successful-tests-with-failed-post");

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var job = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/failed-jobs.json")))!.AsArray())!;
        Assert.Equal("failure", job["conclusion"]!.GetValue<string>());
        Assert.Equal("success", job["steps"]![1]!["conclusion"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
        Assert.Equal("not-applicable", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!["state"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!["failures"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareCollectsPinnedAttemptPagedEvidenceAndExactProvenance()
    {
        var result = await RunCollectionAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!;
        var failures = bundle["test_evidence"]!["failures"]!.AsArray();
        var failure = Assert.Single(failures);
        Assert.Equal("Suite.Case(value: a/b)", failure!["name"]!.GetValue<string>());
        Assert.Equal("77", failure["job_id"]!.GetValue<string>());
        Assert.Equal("00001.trx", failure["sources"]![0]!["result_file"]!.GetValue<string>());
        Assert.Equal("trx", failure["sources"]![0]!["format"]!.GetValue<string>());
        Assert.Equal("stderr evidence", failure["diagnostics"]!["standard_error"]!.GetValue<string>());
        Assert.Equal(2, bundle["run"]!["attempt"]!.GetValue<int>());
        Assert.Equal("42", bundle["subject_pr"]!["number"]!.GetValue<string>());
        var jobs = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/failed-jobs.json")))!.AsArray();
        Assert.Equal(77, Assert.Single(jobs)!["id"]!.GetValue<int>());
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains(calls, c => c.Contains("actions/runs/123/attempts/2", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("actions/runs/123/attempts/2/jobs", StringComparison.Ordinal) && c.Contains("--paginate --slurp", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("actions/artifacts/10/zip", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Contains("commits/failed-sha/pulls", StringComparison.Ordinal) && c.Contains("--paginate --slurp", StringComparison.Ordinal));
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
        Assert.Contains("Error: token=[REDACTED]", await File.ReadAllTextAsync(Path.Combine(data, "diagnostics/job-77.log")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-run", "requested run")]
    [InlineData("wrong-attempt", "requested attempt")]
    [InlineData("history-transport", "Unable to load CI failure history")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareSurfacesRunIdentityAndHistoryTransportFailures(string scenario, string error)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.Output, StringComparison.Ordinal);
        if (scenario == "history-transport")
        {
            Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "collection-outcome.json")))!["has_work"]!.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData("wrong-artifact-run")]
    [InlineData("wrong-artifact-sha")]
    [InlineData("missing-artifact")]
    [InlineData("duplicate-artifact")]
    [InlineData("unsafe-archive")]
    [InlineData("encrypted-archive")]
    [InlineData("zip64-archive")]
    [InlineData("wrong-entry-count")]
    [InlineData("wrong-crc")]
    [InlineData("malformed-trx")]
    [InlineData("missing-trx")]
    [InlineData("malformed-mocha")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareRetainsUnavailableStateForWrongMissingOrMalformedRequiredEvidence(string scenario)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.Equal(0, result.ExitCode);
        var data = EvidenceDirectory;
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!;
        Assert.Equal("unavailable", state["state"]!.GetValue<string>());
        Assert.Empty(state["failures"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(data, "analysis-summary.md")));
    }

    [Theory]
    [InlineData("optional-empty-mocha")]
    [InlineData("optional-missing-mocha")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareKeepsOptionalEmptyExtensionEvidenceAtJobLevel(string scenario)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!["test_evidence"]!;
        Assert.Equal(scenario == "optional-missing-mocha" ? "not-applicable" : "complete", state["state"]!.GetValue<string>());
        if (scenario == "optional-empty-mocha")
        {
            Assert.Empty(state["failures"]!.AsArray());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("not-a-run")]
    [InlineData("9223372036854775808")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareRejectsInvalidRunIdentifierBeforeAnyGitHubCalls(string runId)
    {
        var result = await RunCollectionAsync("manual", runId: runId);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("canonical positive 64-bit decimal text", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareParsesExplicitRunIdentifiersAndAttemptsOnce()
    {
        var result = await RunCollectionAsync(runId: "123", runAttempt: "02");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains(
            "api repos/microsoft/aspire/actions/runs/123/attempts/2",
            await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("manual")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareCollectsMainComparisonAndManualLatestAttempt(string scenario)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")))!;
        Assert.Equal(2, bundle["run"]!["attempt"]!.GetValue<int>());
        if (scenario == "main")
        {
            Assert.Equal("main", bundle["run"]!["scope"]!.GetValue<string>());
            Assert.Null(bundle["subject_pr"]);
            var history = bundle["main_context"]!["candidate_history"]!;
            Assert.Equal("available", history["state"]!.GetValue<string>());
            Assert.Equal("41", Assert.Single(history["candidates"]!.AsArray())!["pull_request"]!["number"]!.GetValue<string>());
        }
        else
        {
            var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
            Assert.Contains("api repos/microsoft/aspire/actions/runs/123", calls);
        }
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PreparePreservesPartialLogAndSafeDownloadFailureAsEvidence()
    {
        var result = await RunCollectionAsync("failed-logs");

        Assert.True(result.ExitCode == 0, result.Output);
        var log = await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/job-77.log"));
        Assert.Equal(
            "Error: token=[REDACTED]\n\nFailed to fetch complete logs for job 77.\n" +
            "GET /repos/microsoft/aspire/actions/jobs/77/logs: response body could not be read completely.",
            log.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("fork", "42")]
    [InlineData("fork-wrong-sha", "")]
    [InlineData("ambiguous", "")]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareUsesExactForkHeadAndNeverGuessesAmbiguousSubject(string scenario, string expected)
    {
        var result = await RunCollectionAsync(scenario);

        Assert.True(result.ExitCode == 0, result.Output);
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json")))!;
        Assert.Equal(expected, bundle["subject_pr"]?["number"]?.GetValue<string>() ?? "");
        if (scenario.StartsWith("fork", StringComparison.Ordinal))
        {
            var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
            Assert.Contains("api --method GET --paginate --slurp repos/microsoft/aspire/pulls -f state=all -f per_page=100 -f head=fork-owner:feature", calls);
        }
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task PrepareReadsCompleteStoredCausesFromMemoryRemote()
    {
        var result = await RunCollectionAsync("stored-history");

        Assert.True(result.ExitCode == 0, result.Output);
        var data = EvidenceDirectory;
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "history/cause-index.json")))!.AsArray();
        Assert.Equal(2, index.Count);
        Assert.Equal(
            """{"id":"keyless-infra","type":"infra-failure","error_pattern":"HTTP 504","occurrences":[{"run_id":11}]}""",
            await File.ReadAllTextAsync(Path.Combine(data, "history/causes", "keyless-infra.json")));
        Assert.Equal(
            """{"id":"opaque-test","type":"flaky-test","test_name":"Opaque/A/B","occurrences":[{"run_id":12}]}""",
            await File.ReadAllTextAsync(Path.Combine(data, "history/causes", "opaque-test.json")));
    }

    private async Task<CommandResult> RunCollectionAsync(
        string scenario = "valid",
        IReadOnlyDictionary<string, string>? environment = null,
        string runId = "123",
        string? runAttempt = "2",
        bool useWorkflowAdapter = false)
    {
        var responses = Directory.CreateDirectory(Path.Combine(_workspace.Path, "responses")).FullName;
        var runnerFailure = scenario.StartsWith("runner-", StringComparison.Ordinal);
        var mocha = scenario.Contains("mocha", StringComparison.Ordinal) || runnerFailure;
        var jobName = scenario == "cancelled-build" ? "Build" :
            scenario == "runner-confirmed-loss-completed-missing-artifact" ? "Tests / No-package tests / Infrastructure (ubuntu-latest)" :
            mocha ? "Tests / VS Code extension E2E (Linux, debug)" : "Tests / No-package tests / Infrastructure (ubuntu-latest)";
        var run = new JsonObject
        {
            ["id"] = scenario == "wrong-run" ? 999 : 123,
            ["run_attempt"] = scenario == "wrong-attempt" ? 3 : 2,
            ["path"] = ".github/workflows/ci.yml",
            ["event"] = scenario == "main" ? "push" : "pull_request",
            ["head_branch"] = scenario == "main" ? "main" : "feature",
            ["head_sha"] = "failed-sha",
            ["workflow_id"] = 7,
            ["created_at"] = "2026-09-04T12:00:00Z",
            ["html_url"] = scenario == "newline-output"
                ? "https://github.com/microsoft/aspire/actions/runs/123\ninjected=true"
                : "https://github.com/microsoft/aspire/actions/runs/123",
            ["conclusion"] = "failure",
            ["pull_requests"] = new JsonArray(),
            ["head_repository"] = new JsonObject { ["owner"] = new JsonObject { ["login"] = "fork-owner" } },
            ["run_started_at"] = "2026-09-04T12:00:00Z",
            ["updated_at"] = "2026-09-04T12:02:00Z",
        };
        await File.WriteAllTextAsync(Path.Combine(responses, "run.json"), run.ToJsonString());
        await File.WriteAllTextAsync(
            Path.Combine(responses, "jobs.json"),
            $$"""
            [{"jobs":[{"id":77,"name":{{JsonSerializer.Serialize(jobName)}},"conclusion":"failure","check_run_url":"https://api.github.com/repos/microsoft/aspire/check-runs/88","steps":[{"name":"Run tests","conclusion":"failure"}]}]},
             {"jobs":[{"id":78,"name":"Final Results","conclusion":"failure","steps":[{"name":"Check dependent jobs failed","conclusion":"failure"}]}]}]
            """);
        if (scenario == "independent-wrappers")
        {
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "jobs.json")))!;
            pages[1]!["jobs"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = 78,
                    ["name"] = "Dependency gate",
                    ["conclusion"] = "failure",
                    ["steps"] = new JsonArray(new JsonObject { ["name"] = "Check dependent jobs failed", ["conclusion"] = "failure" }),
                },
                new JsonObject
                {
                    ["id"] = 79,
                    ["name"] = "Final Results",
                    ["conclusion"] = "failure",
                    ["steps"] = new JsonArray(),
                });
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), pages.ToJsonString());
        }
        if (runnerFailure || scenario is "healthy" or "cancelled-build")
        {
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "jobs.json")))!;
            var job = pages[0]!["jobs"]![0]!;
            job["conclusion"] = scenario switch
            {
                "runner-cancelled" or "runner-fail-fast" or "runner-cancelled-step" or "cancelled-build" => "cancelled",
                "runner-timed-out" => "timed_out",
                "healthy" => "success",
                _ => "failure",
            };
            job["steps"] = scenario is "runner-cancelled" or "cancelled-build" ? new JsonArray() : new JsonArray(
                new JsonObject { ["name"] = "Set up job", ["status"] = "completed", ["conclusion"] = "success" },
                new JsonObject
                {
                    ["name"] = "Run extension E2E tests",
                    ["status"] = scenario is "healthy" or "runner-cancelled-step" or "runner-cancelled-test-with-failed-post" or
                        "runner-timed-out-test-with-failed-post" or "runner-successful-tests-with-failed-post"
                        ? "completed" : scenario == "runner-fail-fast" ? "queued" : "in_progress",
                    ["conclusion"] = scenario switch
                    {
                        "healthy" or "runner-successful-tests-with-failed-post" => "success",
                        "runner-cancelled-step" or "runner-cancelled-test-with-failed-post" => "cancelled",
                        "runner-timed-out-test-with-failed-post" => "timed_out",
                        _ => null,
                    },
                },
                new JsonObject { ["name"] = "Upload E2E diagnostics", ["status"] = "queued", ["conclusion"] = null });
            if (scenario.EndsWith("-with-failed-post", StringComparison.Ordinal))
            {
                job["steps"]!.AsArray().Add(new JsonObject { ["name"] = "Post cleanup", ["status"] = "completed", ["conclusion"] = "failure" });
            }
            if (scenario == "runner-confirmed-loss-completed-missing-artifact")
            {
                foreach (var step in job["steps"]!.AsArray())
                {
                    step!["status"] = "completed";
                    step["conclusion"] = step["name"]!.GetValue<string>() == "Run extension E2E tests" ? "failure" : "success";
                }
            }
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), pages.ToJsonString());
        }
        if (scenario != "missing-step-number")
        {
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "jobs.json")))!.AsArray();
            foreach (var page in pages)
            {
                AnalyzeCiFailureFixtures.AddFixtureStepNumbers(page!["jobs"]!.AsArray());
            }
            if (scenario == "nonpositive-step")
            {
                pages[0]!["jobs"]![0]!["steps"]![0]!["number"] = 0;
            }
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), pages.ToJsonString());
        }
        if (scenario == "malformed-job-rows")
        {
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), """[{"jobs":[null,5]}]""");
        }
        var zip = Path.Combine(responses, "results.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            if (scenario is not "optional-empty-mocha" and not "runner-empty-artifact")
            {
                await WriteZipEntryAsync(
                    archive,
                    scenario == "unsafe-archive" ? "../failure.trx" : scenario == "missing-trx" ? "ignored.txt" : mocha ? "nested/mocha.json" : "nested/failure.trx",
                    scenario is "malformed-trx" or "malformed-mocha" ? "malformed" :
                    """
                    <TestRun><TestDefinitions><UnitTest id="duplicate"/><UnitTest id="duplicate"/></TestDefinitions><Results><UnitTestResult testName="Suite.Case(value: a/b)" outcome="Failed"><Output><ErrorInfo><Message>Assertion failed</Message><StackTrace>frame</StackTrace></ErrorInfo><StdErr>stderr evidence</StdErr></Output></UnitTestResult></Results></TestRun>
                    """);
            }
        }
        if (scenario is "encrypted-archive" or "zip64-archive" or "wrong-entry-count" or "wrong-crc")
        {
            var bytes = await File.ReadAllBytesAsync(zip);
            // This classic ZIP fixture has no archive comment: EOCD is the final
            // 22 bytes; central flags/CRC are at offsets 8/16 after "PK\x01\x02".
            var central = bytes.AsSpan().IndexOf("PK\u0001\u0002"u8);
            Assert.True(central >= 0);
            if (scenario == "encrypted-archive")
            {
                bytes[central + 8] |= 1;
            }
            else if (scenario == "wrong-crc")
            {
                bytes[central + 16] ^= 1;
            }
            else
            {
                var end = bytes.Length - 22;
                var count = scenario == "zip64-archive" ? ushort.MaxValue : (ushort)0;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), count);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), count);
            }
            await File.WriteAllBytesAsync(zip, bytes);
        }
        var artifact = new JsonObject
        {
            ["id"] = 10,
            ["name"] = mocha ? "extension-e2e-diagnostics-linux-x64-debug-attempt2" : "logs-Infrastructure-ubuntu-latest",
            ["expired"] = false,
            ["created_at"] = "2026-09-04T12:01:00Z",
            ["size_in_bytes"] = new FileInfo(zip).Length,
            ["workflow_run"] = new JsonObject { ["id"] = scenario == "wrong-artifact-run" ? 999 : 123, ["head_sha"] = scenario == "wrong-artifact-sha" ? "other-sha" : "failed-sha" },
        };
        var artifacts = new JsonArray();
        if (scenario == "runner-confirmed-loss-invalid-size")
        {
            artifact["size_in_bytes"] = "invalid";
        }
        if (scenario == "runner-confirmed-loss-expired")
        {
            artifact["expired"] = true;
        }
        if (scenario is not "missing-artifact" and not "optional-missing-mocha" && !scenario.EndsWith("-missing-artifact", StringComparison.Ordinal) &&
            scenario != "runner-confirmed-loss-ambiguous-job" &&
            (!runnerFailure || scenario is "runner-empty-artifact" or "runner-download-failure" ||
            scenario.StartsWith("runner-confirmed-loss-", StringComparison.Ordinal)))
        {
            artifacts.Add(artifact);
            if (scenario is "duplicate-artifact" or "runner-confirmed-loss-ambiguous")
            {
                artifacts.Add(artifact.DeepClone());
            }
        }
        if (scenario.StartsWith("runner-confirmed-loss-", StringComparison.Ordinal))
        {
            var otherZip = Path.Combine(responses, "other.zip");
            using (var archive = ZipFile.Open(otherZip, ZipArchiveMode.Create))
            {
                await WriteZipEntryAsync(archive, "results.trx", FrameworkResult("Other.Failure", "other-execution", "real assertion"));
            }
            artifacts.Add(new JsonObject
            {
                ["id"] = 11, ["name"] = "logs-Other-ubuntu-latest", ["expired"] = false,
                ["created_at"] = "2026-09-04T12:01:00Z", ["size_in_bytes"] = new FileInfo(otherZip).Length,
                ["workflow_run"] = new JsonObject { ["id"] = 123, ["head_sha"] = "failed-sha" },
            });
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "jobs.json")))!;
            pages[0]!["jobs"]!.AsArray().Add(new JsonObject
            {
                ["id"] = 79, ["name"] = "Tests / No-package tests / Other (ubuntu-latest)", ["conclusion"] = "failure",
                ["steps"] = new JsonArray(new JsonObject { ["number"] = 1, ["name"] = "Run tests", ["conclusion"] = "failure" }),
            });
            if (scenario == "runner-confirmed-loss-ambiguous-job")
            {
                var duplicate = pages[0]!["jobs"]![0]!.DeepClone();
                duplicate["id"] = 80;
                duplicate.AsObject().Remove("check_run_url");
                pages[0]!["jobs"]!.AsArray().Add(duplicate);
            }
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), pages.ToJsonString());
        }
        await File.WriteAllTextAsync(
            Path.Combine(responses, "artifacts.json"),
            new JsonArray(new JsonObject { ["artifacts"] = artifacts }, new JsonObject { ["artifacts"] = new JsonArray() }).ToJsonString());
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            if [ -f require-final-outputs ] && grep -q '^has_work=' "$GITHUB_OUTPUT"; then
              printf '%s\n' 'Workflow outputs were written before collection finished' >&2
              exit 98
            fi
            printf '%s\n' "$*" >> gh-calls.log
            paths_file="${INVOCATION_PATHS:-$GITHUB_OUTPUT}"
            evidence_dir=$(sed -n 's/^evidence_dir=//p' "$paths_file" | head -n 1)
            if [ -f fail-outcome-write ]; then mkdir -p "$evidence_dir/collection-outcome.json"; fi
            case "$*" in
              "api --help") printf '%s' '--allow-escape-sequences' ;;
              *"actions/runs/123/attempts/2/jobs"*)
                if [ "$PR_SCENARIO" = job-list-api-failure ]; then
                  printf '%s' 'gh: Server Error (HTTP 502)' >&2
                  exit 1
                fi
                cat responses/jobs.json
                ;;
              *"actions/runs/123/attempts/2") cat responses/run.json ;;
              *"actions/runs/123") cat responses/run.json ;;
              *"actions/workflows/7/runs"*) printf '%s' '{"total_count":1,"workflow_runs":[{"id":100,"created_at":"2026-09-04T11:00:00Z","head_sha":"successful-sha"}]}' ;;
              *"compare/successful-sha...failed-sha"*) printf '%s' '[{"status":"ahead","total_commits":1,"commits":[{"sha":"candidate-sha","commit":{"message":"Candidate"},"html_url":"https://github.com/microsoft/aspire/commit/candidate-sha"}]}]' ;;
              *"commits/candidate-sha/pulls"*) printf '%s' '[[{"number":41,"title":"Merged","html_url":"https://github.com/microsoft/aspire/pull/41","merged_at":"2026-09-04T11:30:00Z","base":{"ref":"main","repo":{"full_name":"microsoft/aspire"}}}]]' ;;
              *"commits/failed-sha/pulls"*)
                case "$PR_SCENARIO" in
                  fork*) printf '[[]]' ;;
                  ambiguous) printf '%s' '[[{"number":42,"base":{"repo":{"full_name":"microsoft/aspire"}}},{"number":43,"base":{"repo":{"full_name":"microsoft/aspire"}}}]]' ;;
                  *) printf '%s' '[[],[{"number":42,"base":{"repo":{"full_name":"microsoft/aspire"}}}]]' ;;
                esac
                ;;
              *"repos/microsoft/aspire/pulls -f state=all"*)
                if [ "$PR_SCENARIO" = fork ]; then
                  printf '%s' '[[{"number":42,"head":{"sha":"failed-sha"}}]]'
                else
                  printf '%s' '[[{"number":42,"head":{"sha":"other-sha"}}]]'
                fi
                ;;
              *"actions/jobs/77/logs"*)
                if [ "$PR_SCENARIO" = failed-noisy-logs ]; then
                  for ((i=1; i<=300; i++)); do printf 'Build FAILED: earlier failure %s\n' "$i"; done
                else
                  printf '%s\n' 'Error: token=collector-secret'
                fi
                if [ "${LOG_FAILURE:-false}" = true ]; then printf '%s\n' 'fetch failed: token=collector-secret' >&2; exit 23; fi
                ;;
              *"actions/jobs/79/logs"*) printf '%s\n' 'Other test failure' ;;
              *"check-runs/88/annotations"*)
                case "$PR_SCENARIO" in
                  runner-missing-diagnostics) printf '%s\n' 'injected annotations lookup failure' >&2; exit 23 ;;
                  runner-confirmed-loss*) printf '%s' '[[{"message":"The hosted runner lost communication with the server."}],[]]' ;;
                  runner-fail-fast) printf '%s' '[[{"message":"Cancelled after another matrix job failed"}],[]]' ;;
                  runner-*) printf '%s' '[[{"message":"Runner lost communication"}],[]]' ;;
                  *) printf '%s' '[[],[]]' ;;
                esac
                ;;
              *"pulls/42/files"*) printf '%s' '[[{"filename":"first.cs","status":"modified"}],[{"filename":"second.cs","status":"added"}]]' ;;
              *"pulls/42")
                if [ "$PR_SCENARIO" = missing-pr-metadata ]; then printf 'not JSON';
                elif [ "$PR_SCENARIO" = wrong-pr-metadata ]; then printf '{"number":43,"title":"Wrong PR"}';
                else printf '%s' '{"number":42,"title":"PR","state":"open","locked":false,"user":{"login":"user"},"head":{"ref":"feature"},"base":{"ref":"main"}}'; fi ;;
              *"actions/runs/123/artifacts"*)
                if [[ "$PR_SCENARIO" = write-evidence-failure* ]]; then mkdir "$evidence_dir/evidence.json"; fi
                cat responses/artifacts.json ;;
              *"actions/artifacts/10/zip")
                if [ "$PR_SCENARIO" = runner-download-failure ]; then printf '%s\n' 'injected download failure' >&2; exit 23; fi
                cat responses/results.zip ;;
              *"actions/artifacts/11/zip") cat responses/other.zip ;;
              *) printf 'Unexpected gh arguments: %s\n' "$*" >&2; exit 99 ;;
            esac
            """);
        Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "init"])).ExitCode);
        Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "init", "--bare", "remote.git"])).ExitCode);
        Assert.Equal(
            0,
            (await RunProcessAsync(
                "git",
                ["--no-pager", "remote", "add", "origin",
            Path.Combine(_workspace.Path, scenario == "history-transport" ? "missing.git" : "remote.git")])).ExitCode);
        if (scenario.StartsWith("stored-history", StringComparison.Ordinal))
        {
            Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "clone", "remote.git", "history-seed"])).ExitCode);
            var causes = Directory.CreateDirectory(Path.Combine(_workspace.Path, "history-seed", "causes")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(causes, "keyless-infra.json"),
                """{"id":"keyless-infra","type":"infra-failure","error_pattern":"HTTP 504","occurrences":[{"run_id":11}]}""");
            await File.WriteAllTextAsync(
                Path.Combine(causes, "opaque-test.json"),
                """{"id":"opaque-test","type":"flaky-test","test_name":"Opaque/A/B","occurrences":[{"run_id":12}]}""");
            if (scenario is "stored-history-invalid-utf8" or "stored-history-utf16")
            {
                var name = scenario == "stored-history-utf16" ? "evidence.json" : "unreadable.json";
                await File.WriteAllBytesAsync(Path.Combine(causes, name), HistoricalAttachmentBytes(scenario == "stored-history-utf16"));
                Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "-C", "history-seed", "add", "--", "causes/" + name])).ExitCode);
            }
            Assert.Equal(
                0,
                (await RunProcessAsync(
                    "git",
                    ["--no-pager", "-C", "history-seed", "add", "--",
                "causes/keyless-infra.json", "causes/opaque-test.json"])).ExitCode);
            Assert.Equal(
                0,
                (await RunProcessAsync(
                    "git",
                    ["--no-pager", "-C", "history-seed",
                "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "Seed history fixture"])).ExitCode);
            Assert.Equal(
                0,
                (await RunProcessAsync(
                    "git",
                    ["--no-pager", "-C", "history-seed",
                "push", "origin", "HEAD:memory/ci-failure-analysis"])).ExitCode);
        }

        var variables = new Dictionary<string, string>
        {
            ["REPO"] = "invalid-ambient-repository",
            ["GITHUB_REPOSITORY"] = useWorkflowAdapter ? "microsoft/aspire" : "invalid-ambient-repository",
            ["GITHUB_ACTIONS"] = useWorkflowAdapter ? "true" : "",
            ["EVENT_NAME"] = scenario == "manual" ? "workflow_dispatch" : "workflow_run",
            ["MANUAL_RUN_ID"] = "123",
            ["LOG_FAILURE"] = scenario is "failed-logs" or "failed-noisy-logs" or "runner-missing-diagnostics" ? "true" : "false",
            ["PR_SCENARIO"] = scenario,
            ["WORKFLOW_RUN_ID"] = "123",
            ["WORKFLOW_RUN_ATTEMPT"] = "2",
            ["GITHUB_OUTPUT"] = Path.Combine(_workspace.Path, "workflow-output"),
            ["INVOCATION_PATHS"] = useWorkflowAdapter ? Path.Combine(_workspace.Path, "workflow-output") : Path.Combine(_workspace.Path, "invocation-paths"),
            ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            variables[name] = value;
        }

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        List<string> command =
        [
            "exec", "--runtimeconfig", Path.ChangeExtension(typeof(AnalyzeCiFailureWorkflowTests).Assembly.Location, ".runtimeconfig.json"),
            typeof(AnalyzeCiFailure.Program).Assembly.Location,
        ];
        if (useWorkflowAdapter)
        {
            command.AddRange(["collect", "--repo", variables["GITHUB_REPOSITORY"],
                "--run-id", scenario == "manual" ? variables["MANUAL_RUN_ID"] : variables["WORKFLOW_RUN_ID"],
                "--github-output", variables["GITHUB_OUTPUT"]]);
            if (scenario != "manual")
            {
                command.AddRange(["--run-attempt", variables["WORKFLOW_RUN_ATTEMPT"]]);
            }
            return await RunProcessAsync(dotnet, command, variables);
        }
        command.AddRange(["collect", "--repo", "microsoft/aspire", "--run-id", runId,
            "--github-output", variables["INVOCATION_PATHS"]]);
        if (scenario != "manual" && runAttempt is not null)
        {
            command.AddRange(["--run-attempt", runAttempt]);
        }

        return await RunProcessAsync(dotnet, command, variables);
    }
}
