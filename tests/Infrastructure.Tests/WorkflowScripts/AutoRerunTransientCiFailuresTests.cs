// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/auto-rerun-transient-ci-failures.js.
/// </summary>
public sealed class AutoRerunTransientCiFailuresTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public AutoRerunTransientCiFailuresTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "auto-rerun-transient-ci-failures.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherNormalPullRequestPolicyReadsAttemptJobsAndAnnotations()
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                forceRerunAll = false,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 1,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = new[] { new { number = 15110 } }
                },
                jobsByPage = new[]
                {
                    new[]
                    {
                        new
                        {
                            id = 11,
                            name = "Build packages",
                            conclusion = "failure",
                            check_run_url = "https://api.github.com/repos/microsoft/aspire/check-runs/99",
                            steps = new[] { new { name = "Checkout code", conclusion = "failure" } }
                        }
                    },
                    new[]
                    {
                        new
                        {
                            id = 12,
                            name = "Build packages 2",
                            conclusion = "failure",
                            check_run_url = "https://api.github.com/repos/microsoft/aspire/check-runs/100",
                            steps = new[] { new { name = "Checkout code", conclusion = "failure" } }
                        }
                    }
                },
                issueStatesByNumber = new Dictionary<string, string> { ["15110"] = "open" },
                annotations = new[] { new { message = "fatal: expected 'packfile'" } }
            });

        JsonElement analysis = result.GetProperty("analysis");
        Assert.Equal("pull-request", analysis.GetProperty("policy").GetString());
        Assert.False(analysis.GetProperty("forceRerunAll").GetBoolean());
        Assert.True(analysis.GetProperty("executionEligible").GetBoolean());
        Assert.Collection(
            analysis.GetProperty("retryableJobs").EnumerateArray(),
            job => Assert.Equal("Build packages", job.GetProperty("name").GetString()),
            job => Assert.Equal("Build packages 2", job.GetProperty("name").GetString()));
        Assert.Collection(
            result.GetProperty("requests").EnumerateArray(),
            r => Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/check-runs/{check_run_id}/annotations", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/check-runs/{check_run_id}/annotations", r.GetProperty("route").GetString()));
    }

    [Theory]
    [InlineData("pull_request", "feature", 4)]
    [InlineData("push", "main", 2)]
    [RequiresTools(["node"])]
    public async Task DispatcherAttemptCapsPreventJobReadsAndWrites(string sourceEvent, string branch, int attempt)
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = sourceEvent,
                    conclusion = "failure",
                    run_attempt = attempt,
                    head_branch = branch,
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123"
                }
            });

        Assert.False(result.GetProperty("analysis").GetProperty("executionEligible").GetBoolean());
        Assert.Empty(result.GetProperty("requests").EnumerateArray());
        Assert.Contains(
            result.GetProperty("messages").EnumerateArray(),
            message => message.GetString()?.Contains("attempt", StringComparison.OrdinalIgnoreCase) is true);
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "raw" &&
                e.GetProperty("text").GetString()?.Contains("attempt", StringComparison.OrdinalIgnoreCase) is true);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [RequiresTools(["node"])]
    public async Task DispatcherForceModeBypassesJobReadsAndRespectsDryRun(bool dryRun, bool expectedExecution)
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                eventName = "workflow_dispatch",
                runId = 123,
                dryRun,
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 1,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = new[] { new { number = 15110 } }
                },
                issueStatesByNumber = new Dictionary<string, string> { ["15110"] = "open" },
                currentRunResponses = new[]
                {
                    new { run_attempt = 1 },
                    new { run_attempt = 2 }
                }
            });

        Assert.Equal("pull-request", result.GetProperty("analysis").GetProperty("policy").GetString());
        Assert.Equal(expectedExecution, result.GetProperty("analysis").GetProperty("executionEligible").GetBoolean());
        Assert.Collection(
            result.GetProperty("analysisRequests").EnumerateArray(),
            request => Assert.Equal("getWorkflowRun", request.GetProperty("route").GetString()),
            request => Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", request.GetProperty("route").GetString()));
        Assert.Equal(
            expectedExecution ? 1 : 0,
            result.GetProperty("requests").EnumerateArray().Count(r =>
                r.GetProperty("route").GetString() == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherManualDryRunRejectsClosedAssociatedPullRequestDuringAnalysis()
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                eventName = "workflow_dispatch",
                runId = 123,
                dryRun = true,
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 1,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = new[] { new { number = 15110 } }
                },
                issueStatesByNumber = new Dictionary<string, string> { ["15110"] = "closed" }
            });

        Assert.False(result.GetProperty("analysis").GetProperty("executionEligible").GetBoolean());
        Assert.DoesNotContain(
            result.GetProperty("requests").EnumerateArray(),
            request => request.GetProperty("route").GetString() ==
                "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "heading" &&
                e.GetProperty("text").GetString() == "Rerun skipped");
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "raw" &&
                e.GetProperty("text").GetString()?.Contains(
                    "All associated pull requests are closed. No jobs were rerun.",
                    StringComparison.Ordinal) is true);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherMainPolicyIgnoresForceModeAndRunsFreshnessChecksBeforeWriting()
    {
        object run = new
        {
            id = 123,
            name = "CI",
            @event = "push",
            conclusion = "failure",
            run_attempt = 1,
            run_number = 7,
            workflow_id = 42,
            path = ".github/workflows/ci.yml",
            head_branch = "main",
            head_sha = "main-sha",
            html_url = "https://github.com/microsoft/aspire/actions/runs/123"
        };
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                workflowRun = run,
                forceRerunAll = true,
                execute = true,
                jobs = new[]
                {
                    new
                    {
                        id = 11,
                        name = "Build packages",
                        conclusion = "failure",
                        check_run_url = "https://api.github.com/repos/microsoft/aspire/check-runs/99",
                        steps = Array.Empty<object>()
                    }
                },
                annotations = new[] { new { message = "The hosted runner lost communication with the server." } },
                currentRun = run,
                currentMainSha = "main-sha",
                mainWorkflowRuns = new[] { new { id = 123, run_number = 7 } }
            });

        JsonElement analysis = result.GetProperty("analysis");
        Assert.Equal("main", analysis.GetProperty("policy").GetString());
        Assert.False(analysis.GetProperty("forceRerunAll").GetBoolean());
        Assert.True(analysis.GetProperty("executionEligible").GetBoolean());
        Assert.Collection(
            result.GetProperty("analysisRequests").EnumerateArray(),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}/jobs", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/check-runs/{check_run_id}/annotations", r.GetProperty("route").GetString()));
        Assert.Collection(
            result.GetProperty("requests").EnumerateArray().Skip(2),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/git/ref/{ref}", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/git/ref/{ref}", r.GetProperty("route").GetString()),
            r => Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}", r.GetProperty("route").GetString()),
            r => Assert.Equal("POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs", r.GetProperty("route").GetString()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherMainAllowlistVetoReportsLogAndSummary()
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "push",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 1,
                    run_number = 7,
                    workflow_id = 42,
                    path = ".github/workflows/ci.yml",
                    head_branch = "main",
                    head_sha = "main-sha",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123"
                },
                jobs = new[]
                {
                    new
                    {
                        id = 11,
                        name = "Tests / No-package tests / Hosting-6 (ubuntu-latest)",
                        conclusion = "failure",
                        check_run_url = "https://api.github.com/repos/microsoft/aspire/check-runs/99",
                        steps = new[] { new { name = "Run tests (Linux/macOS)", conclusion = "failure" } }
                    }
                },
                annotations = new[] { new { message = "A deterministic test assertion failed." } }
            });

        Assert.False(result.GetProperty("analysis").GetProperty("executionEligible").GetBoolean());
        Assert.Contains(
            result.GetProperty("messages").EnumerateArray(),
            message => message.GetString()?.Contains(
                "narrow current-main transient infrastructure allowlist",
                StringComparison.Ordinal) is true);
        Assert.DoesNotContain(
            result.GetProperty("messages").EnumerateArray(),
            message => message.GetString()?.Contains("Failed to fetch logs", StringComparison.Ordinal) is true);
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "heading" &&
                e.GetProperty("text").GetString() == "Rerun skipped");
    }

    [Theory]
    [InlineData("workflow_run", "pull_request", "feature", "completed", "failure", "pull-request")]
    [InlineData("workflow_run", "push", "main", "completed", "failure", "main")]
    [InlineData("workflow_dispatch", "pull_request", "feature", "completed", "failure", "pull-request")]
    [InlineData("workflow_dispatch", "push", "main", "completed", "failure", "pull-request")]
    [InlineData("workflow_dispatch", "pull_request", "feature", "completed", "success", null)]
    [InlineData("workflow_dispatch", "pull_request", "feature", "in_progress", null, null)]
    [InlineData("workflow_run", "push", "release/13", "completed", "failure", null)]
    [InlineData("workflow_run", "schedule", "main", "completed", "failure", null)]
    [InlineData("workflow_run", "push", "main", "completed", "success", null)]
    [RequiresTools(["node"])]
    public async Task DispatcherSelectsPolicyFromTrustedRunContext(
        string eventName, string sourceEvent, string branch, string status, string? conclusion, string? expectedPolicy)
    {
        string? policy = await InvokeHarnessAsync<string?>(
            "selectPolicy",
            new
            {
                eventName,
                owner = "microsoft",
                repo = "aspire",
                workflowRun = new { name = "CI", @event = sourceEvent, head_branch = branch, status, conclusion }
            });

        Assert.Equal(expectedPolicy, policy);
    }

    [Theory]
    [InlineData("completed", "success")]
    [InlineData("in_progress", null)]
    [RequiresTools(["node"])]
    public async Task DispatcherManualPolicySkipsRunsThatAreNotCompletedFailures(string status, string? conclusion)
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                eventName = "workflow_dispatch",
                runId = 123,
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status,
                    conclusion,
                    run_attempt = 1,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = new[] { new { number = 15110 } }
                },
                issueStatesByNumber = new Dictionary<string, string> { ["15110"] = "open" }
            });

        Assert.False(result.TryGetProperty("analysis", out _));
        Assert.Equal("false", result.GetProperty("outputs").GetProperty("rerun_execution_eligible").GetString());
        Assert.Collection(
            result.GetProperty("requests").EnumerateArray(),
            request => Assert.Equal("getWorkflowRun", request.GetProperty("route").GetString()));
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "raw" &&
                e.GetProperty("text").GetString()?.Contains("completed failed CI run", StringComparison.Ordinal) is true);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherPullRequestPolicySkipsWhenAttemptAdvancesBeforeRerunPost()
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                eventName = "workflow_dispatch",
                runId = 123,
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 3,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = new[] { new { number = 15110 } }
                },
                issueStatesByNumber = new Dictionary<string, string> { ["15110"] = "open" },
                currentRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 4,
                    head_branch = "feature"
                }
            });

        Assert.DoesNotContain(
            result.GetProperty("requests").EnumerateArray(),
            request => request.GetProperty("route").GetString() ==
                "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "raw" &&
                e.GetProperty("text").GetString()?.Contains("advanced from attempt 3 to attempt 4", StringComparison.Ordinal) is true);
        Assert.Contains(
            result.GetProperty("messages").EnumerateArray(),
            message => message.GetString()?.Contains(
                "advanced from attempt 3 to attempt 4",
                StringComparison.Ordinal) is true);
    }

    [Theory]
    [InlineData("workflow_run")]
    [InlineData("workflow_dispatch")]
    [RequiresTools(["node"])]
    public async Task DispatcherPullRequestPolicySummarizesMissingAssociatedPullRequest(string eventName)
    {
        JsonElement result = await InvokeHarnessAsync<JsonElement>(
            "dispatchWorkflow",
            new
            {
                eventName,
                runId = 123,
                forceRerunAll = true,
                execute = true,
                workflowRun = new
                {
                    id = 123,
                    name = "CI",
                    @event = "pull_request",
                    status = "completed",
                    conclusion = "failure",
                    run_attempt = 1,
                    head_branch = "feature",
                    html_url = "https://github.com/microsoft/aspire/actions/runs/123",
                    pull_requests = Array.Empty<object>()
                }
            });

        Assert.False(result.GetProperty("analysis").GetProperty("executionEligible").GetBoolean());
        Assert.DoesNotContain(
            result.GetProperty("requests").EnumerateArray(),
            request => request.GetProperty("route").GetString() ==
                "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Contains(
            result.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("type").GetString() == "raw" &&
                e.GetProperty("text").GetString()?.Contains("No associated pull request", StringComparison.Ordinal) is true);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DispatcherRejectsAutomaticMainRunsOutsideAspireRepository()
    {
        string? policy = await InvokeHarnessAsync<string?>(
            "selectPolicy",
            new
            {
                eventName = "workflow_run",
                owner = "microsoft",
                repo = "another-repository",
                workflowRun = new { name = "CI", @event = "push", head_branch = "main", conclusion = "failure" }
            });

        Assert.Null(policy);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetriesJobLevelInfrastructureFailureWithNoFailedSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Job-level runner or infrastructure failure matched the transient allowlist.", result.RetryableJobs[0].Reason);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetriesRetrySafeFailedStepWhenAnnotationsMatchTransientSignature()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Checkout code"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "fatal: expected 'packfile'");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Failed step 'Checkout code' matched the transient annotation allowlist.", result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SkipsJobsWhoseFailedStepsAreOutsideRetrySafeAllowlist()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Compile project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal("Failed step 'Compile project' is not covered by the retry-safe rerun rules.", result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SkipsRetrySafeStepsWhenAnnotationsAreGeneric()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Set up .NET Core"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code 1.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal("Failed step 'Set up .NET Core' did not include a retry-safe transient infrastructure signal in the job annotations.", result.SkippedJobs[0].Reason);
    }

    [Theory]
    [InlineData("Final Results")]
    [InlineData("Tests / Final Test Results")]
    [RequiresTools(["node"])]
    public async Task IgnoresConfiguredAggregatorJobsEntirely(string jobName)
    {
        WorkflowJob job = CreateJob(name: jobName, failedSteps: ["Set up job"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Empty(result.FailedJobs);
        Assert.Empty(result.RetryableJobs);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task KeepsMixedFailureVetoWhenIgnoredTestStepsFailAlongsideRetrySafeSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)", "Upload logs, and test results"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Failed to CreateArtifact: Unable to make request: ENOTFOUND");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed steps 'Run tests (Windows) | Upload logs, and test results' include a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowOverrideForExplicitJobLevelInfrastructureAnnotationsOnIgnoredSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "The hosted runner lost communication with the server.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Ignored failed step 'Run tests (Windows)' matched the job-level infrastructure override allowlist.", result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowOverrideForWindowsPostTestCleanupProcessInitializationFailures()
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Upload logs, and test results",
            "Copy CLI E2E recordings for upload",
            "Upload CLI E2E recordings",
            "Generate test results summary",
            "Post Checkout code"
        ]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code -1073741502.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Post-test cleanup steps 'Upload logs, and test results | Copy CLI E2E recordings for upload | Upload CLI E2E recordings | Generate test results summary | Post Checkout code' matched the Windows process initialization failure override allowlist.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotOverrideWindowsProcessInitializationFailuresWhenTestExecutionAlsoFailed()
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Run tests (Windows)",
            "Upload logs, and test results",
            "Generate test results summary"
        ]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, "Process completed with exit code -1073741502.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed steps 'Run tests (Windows) | Upload logs, and test results | Generate test results summary' include a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsNarrowLogBasedOverrideForDncengFeedServiceIndexFailuresInIgnoredBuildSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed step 'Build test project' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Theory]
    [InlineData("Install sdk for nuget based testing")]
    [InlineData("Build with packages")]
    [InlineData("Run TypeScript SDK validation")]
    [InlineData("Build Python validation image")]
    [RequiresTools(["node"])]
    public async Task AllowsSameFeedOverrideForOtherCiBootstrapBuildAndValidationSteps(string failedStep)
    {
        WorkflowJob job = CreateJob(failedSteps: [failedStep]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            $"Failed step '{failedStep}' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsSameNetworkOverrideForBroaderBootstrapStepsOutsideTheOldAllowlist()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run ./.github/actions/enumerate-tests"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed step 'Run ./.github/actions/enumerate-tests' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesPluralFailedStepLabelWhenMultipleFailedStepsShareALogBasedOverride()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project", "Check validation results"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed steps 'Build test project | Check validation results' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to load the service index for source https:\\/\\/(?:pkgs\\.dev\\.azure\\.com\\/dnceng|dnceng\\.pkgs\\.visualstudio\\.com)\\/public\\/_packaging\\//i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AllowsLogBasedOverrideForConnectionResetByPeerInBuildSteps()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build RID-specific packages"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "Unhandled exception: Unable to read data from the transport connection: Connection reset by peer.");

        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Failed step 'Build RID-specific packages' will be retried because the job log shows a likely transient infrastructure network failure. Matched pattern: `/Unable to read data from the transport connection: Connection reset by peer/i`.",
            result.RetryableJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesLongerMarkdownFenceWhenMatchedPatternContainsBackticks()
    {
        string result = await InvokeHarnessAsync<string>(
            "formatMatchedPatternForMarkdown",
            new
            {
                matchedPattern = "/foo`bar/i"
            });

        Assert.Equal(" Matched pattern: ``/foo`bar/i``.", result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotApplyBroadNetworkOverrideWhenTestExecutionFailed()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Windows)"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed step 'Run tests (Windows)' includes a test execution failure, so the job was not retried without a high-confidence infrastructure override.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotRetryIgnoredBuildStepsWhenTheLogLacksFeedNetworkSignature()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(
            job,
            "Process completed with exit code 1.",
            "error MSB4236: The SDK specified could not be found.");

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "Failed step 'Build test project' is only retried when the job shows a high-confidence infrastructure override, and none was found.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UsesAvailableDiagnosticsMessageWhenNoSignalsWereInspectedFromFailedStepsOrLogs()
    {
        WorkflowJob job = CreateJob(failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeSingleJobAsync(job, string.Empty);

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "No retry-safe transient infrastructure signal was found in the available job diagnostics.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ContinuesInspectingLaterLogsWhenEarlierCandidatesAreNotRetryable()
    {
        WorkflowJob firstJob = CreateJob(id: 1, failedSteps: ["Build test project"]);
        WorkflowJob secondJob = CreateJob(id: 2, failedSteps: ["Build test project"]);

        AnalyzeFailedJobsResult result = await AnalyzeJobsAsync(
            [firstJob, secondJob],
            new Dictionary<string, string>
            {
                ["1"] = "Process completed with exit code 1.",
                ["2"] = "Process completed with exit code 1."
            },
            new Dictionary<string, string>
            {
                ["1"] = "error MSB4236: The SDK specified could not be found.",
                ["2"] = "error : Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json."
            },
            maxRetryableJobs: 1);

        Assert.Equal([1, 2], result.LogRequestJobIds);
        Assert.Single(result.RetryableJobs);
        Assert.Equal(2, result.RetryableJobs[0].Id);
        Assert.Equal(1, result.LogRequestJobIds[0]);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(1, result.SkippedJobs[0].Id);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRetriesExactHostedRunnerLossOutsideExcludedHostingShards()
    {
        WorkflowJob job = CreateJob(
            name: "Tests / No-package tests / Hosting-6 (windows-latest)",
            failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string>
            {
                ["1"] = "The hosted runner lost communication with the server. Anything in your workflow that terminates the runner process can cause this error."
            });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal("The job annotation matched GitHub's hosted-runner communication-loss signal.", result.RetryableJobs[0].Reason);
        Assert.Equal([1], result.AnnotationRequestJobIds);
        Assert.Empty(result.LogRequestJobIds);
    }

    [Theory]
    [InlineData("connect: connection refused")]
    [InlineData("Connection reset by peer")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRetriesOnlyNarrowAzureContainerRegistryTransportFailures(string transportFailure)
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string>
            {
                ["1"] = $"failed to pull netaspireci.azurecr.io/aspire/testimage:latest: {transportFailure}"
            });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal("The job diagnostics matched an Azure Container Registry transport failure.", result.RetryableJobs[0].Reason);
        Assert.Equal([1], result.LogRequestJobIds);
    }

    [Theory]
    [InlineData("HTTP 503")]
    [InlineData("ServiceUnavailable")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRetriesMicrosoftContainerRegistryOnlyWithServiceUnavailableStatus(string serviceUnavailable)
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string>
            {
                ["1"] = $"CONTAINER1014: failed to pull mcr.microsoft.com/dotnet/aspnet:latest: {serviceUnavailable}"
            });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal("The job diagnostics matched a Microsoft Container Registry service-unavailable response.", result.RetryableJobs[0].Reason);
    }

    [Theory]
    [InlineData(
        "connect: connection refused while contacting netaspireci.azurecr.io/aspire/testimage:latest",
        "The job diagnostics matched an Azure Container Registry transport failure.")]
    [InlineData(
        "ServiceUnavailable response from mcr.microsoft.com/dotnet/aspnet:latest",
        "The job diagnostics matched a Microsoft Container Registry service-unavailable response.")]
    [RequiresTools(["node"])]
    public async Task CurrentMainMatchesBoundedRegistryFailureWhenSignalPrecedesHostname(string jobLogText, string expectedReason)
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = jobLogText });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal(expectedReason, result.RetryableJobs[0].Reason);
    }

    [Theory]
    [InlineData(
        "failed to pull netaspireci.azurecr.io/aspire/testimage:latest: connect: connection refused",
        "The job diagnostics matched an Azure Container Registry transport failure.")]
    [InlineData(
        "CONTAINER1014: failed to pull mcr.microsoft.com/dotnet/aspnet:latest: HTTP 503",
        "The job diagnostics matched a Microsoft Container Registry service-unavailable response.")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRegistryDiagnosticsCanMatchAnnotationsWithoutFetchingLogs(
        string annotationText,
        string expectedReason)
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = annotationText });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal(expectedReason, result.RetryableJobs[0].Reason);
        Assert.Empty(result.LogRequestJobIds);
    }

    [Theory]
    [InlineData("-1073741502")]
    [InlineData("0xC0000142")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRetriesWindowsProcessInitializationFailureOnlyAfterTestsCompleted(string exitCode)
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Check for hang dump files",
            "Upload logs, and test results",
            "Copy CLI E2E recordings for upload",
            "Upload CLI E2E recordings",
            "Generate test results summary",
            "Post Checkout code"
        ], successfulSteps: ["Run tests (Windows)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = $"Process completed with exit code {exitCode}." });

        Assert.True(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Equal(
            "Post-test/reporting steps 'Check for hang dump files | Upload logs, and test results | Copy CLI E2E recordings for upload | Upload CLI E2E recordings | Generate test results summary | Post Checkout code' matched the Windows process initialization failure signal.",
            result.RetryableJobs[0].Reason);
        Assert.Empty(result.LogRequestJobIds);
    }

    [Theory]
    [InlineData("Hosting-1")]
    [InlineData("Hosting-5")]
    [RequiresTools(["node"])]
    public async Task CurrentMainNeverRetriesRunnerLossForExcludedHostingShards(string hostingShard)
    {
        WorkflowJob job = CreateJob(
            name: $"Tests / No-package tests / {hostingShard} (windows-latest)",
            failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string>
            {
                ["1"] = "The hosted runner lost communication with the server. Anything in your workflow that terminates the runner process can cause this error."
            });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Contains("excluded from automatic main reruns", result.SkippedJobs[0].Reason);
        Assert.Empty(result.AnnotationRequestJobIds);
        Assert.Empty(result.LogRequestJobIds);
    }

    [Theory]
    [InlineData("netaspireci.azurecr.io/aspire/testimage:latest: Operation timed out")]
    [InlineData("CONTAINER1014: failed to pull mcr.microsoft.com/dotnet/aspnet:latest")]
    [InlineData("CONTAINER1014: ServiceUnavailable while pulling docker.io/library/redis:latest")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRejectsGenericRegistryAndContainerFailures(string jobLogText)
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = jobLogText });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainDoesNotCorrelateDistantAcrHostnameWithAnotherEndpointsTransportFailure()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);
        string jobLogText =
            $"Successfully authenticated to netaspireci.azurecr.io.{new string('x', 501)}" +
            "Failed to pull docker.io/library/redis:latest: connect: connection refused";

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = jobLogText });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainDoesNotCorrelateAnotherEndpointsServiceFailureWithDistantMcrHostname()
    {
        WorkflowJob job = CreateJob(failedSteps: ["Run tests (Linux/macOS)"]);
        string jobLogText =
            $"docker.io/library/redis:latest returned HTTP 503.{new string('x', 501)}" +
            "Successfully pulled mcr.microsoft.com/dotnet/aspnet:latest";

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = jobLogText });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRejectsWindowsProcessInitializationFailureDuringTestExecution()
    {
        WorkflowJob job = CreateJob(failedSteps:
        [
            "Run tests (Windows)",
            "Upload logs, and test results"
        ]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code -1073741502." });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("skipped")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRejectsWindowsProcessInitializationFailureWithoutSuccessfulTests(string testStepState)
    {
        string[] reportingFailures =
        [
            "Upload logs, and test results",
            "Generate test results summary"
        ];
        WorkflowJob job = testStepState switch
        {
            "skipped" => CreateJob(
                failedSteps: reportingFailures,
                skippedSteps: ["Run tests (Windows)"]),
            _ => CreateJob(failedSteps: reportingFailures),
        };

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code -1073741502." });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(
            "The job did not match the narrow current-main transient infrastructure allowlist.",
            result.SkippedJobs[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainVetoesWholeRunWhenTransientInfrastructureAndRealTestFailuresAreMixed()
    {
        WorkflowJob infrastructureFailure = CreateJob(
            id: 1,
            name: "Tests / No-package tests / Hosting-6 (windows-latest)",
            failedSteps: []);
        WorkflowJob testFailure = CreateJob(
            id: 2,
            name: "Tests / No-package tests / Sample (ubuntu-latest)",
            failedSteps: ["Run tests (Linux/macOS)"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [infrastructureFailure, testFailure],
            new Dictionary<string, string>
            {
                ["1"] = "The hosted runner lost communication with the server. Anything in your workflow that terminates the runner process can cause this error.",
                ["2"] = "Process completed with exit code 1."
            },
            new Dictionary<string, string>
            {
                ["2"] = "Test Run Failed. Failed: 1."
            });

        Assert.False(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Equal(2, result.SkippedJobs[0].Id);
    }

    [Theory]
    [InlineData("Final Results")]
    [InlineData("Tests / Final Test Results")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRequiresRealFailedJobsAfterIgnoringAggregateResults(string aggregateJobName)
    {
        WorkflowJob job = CreateJob(name: aggregateJobName, failedSteps: ["Set up job"]);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string>
            {
                ["1"] = "The hosted runner lost communication with the server."
            });

        Assert.False(result.RerunEligible);
        Assert.Empty(result.FailedJobs);
        Assert.Empty(result.RetryableJobs);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainPreservesRetryableJobCapForStrictWholeRunEligibility()
    {
        WorkflowJob[] jobs = Enumerable.Range(1, 6)
            .Select(id => CreateJob(
                id: id,
                name: $"Tests / No-package tests / Sample-{id} (ubuntu-latest)",
                failedSteps: []))
            .ToArray();
        Dictionary<string, string> annotations = jobs.ToDictionary(
            job => job.Id.ToString(),
            _ => "The hosted runner lost communication with the server.");

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(jobs, annotations, maxRetryableJobs: 5);

        Assert.False(result.RerunEligible);
        Assert.Equal(6, result.RetryableJobs.Length);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainSourceAttemptTwoCannotBecomeEligibleAfterClassification()
    {
        WorkflowJob job = CreateJob(failedSteps: []);

        AnalyzeFailedJobsResult result = await AnalyzeMainJobsAsync(
            [job],
            new Dictionary<string, string>
            {
                ["1"] = "The hosted runner lost communication with the server."
            },
            runAttempt: 2);

        Assert.False(result.RerunEligible);
        Assert.Single(result.RetryableJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobParsesCheckRunIdFromWorkflowJobPayload()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob
                {
                    Id = 10,
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/check-runs/123456789"
                }
            });

        Assert.Equal(123456789, checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobFallsBackToLoadingWorkflowJobWhenNeeded()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob { Id = 42 },
                workflowJob = new WorkflowJob
                {
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/check-runs/987654321"
                }
            });

        Assert.Equal(987654321, checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetCheckRunIdForJobReturnsNullWhenNoCheckRunIdCanBeResolved()
    {
        int? checkRunId = await InvokeHarnessAsync<int?>(
            "getCheckRunIdForJob",
            new
            {
                job = new WorkflowJob
                {
                    Id = 42,
                    CheckRunUrl = "https://api.github.com/repos/microsoft/aspire/actions/jobs/42"
                },
                workflowJob = new WorkflowJob()
            });

        Assert.Null(checkRunId);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersPrefersWorkflowRunPayloadWhenPresent()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    pull_requests = new[]
                    {
                        new { number = 15110 },
                        new { number = 15110 },
                        new { number = 15111 }
                    }
                }
            });

        Assert.Equal([15110, 15111], result.PullRequestNumbers);
        Assert.Empty(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersFallsBackToHeadLookupWhenPayloadOmitsPullRequests()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_sha = "5ccc5540dcc68f57dbe10ff941d1f39bab9f9336",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHead = new Dictionary<string, object[]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        new
                        {
                            number = 13832,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "5ccc5540dcc68f57dbe10ff941d1f39bab9f9336",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        }
                    ]
                }
            });

        Assert.Equal([13832], result.PullRequestNumbers);

        RequestRecord request = Assert.Single(result.Requests);
        Assert.Equal("GET /repos/{owner}/{repo}/pulls", request.Route);
        Assert.Equal("radical:conditional-test-runs", request.Payload.GetProperty("head").GetString());
        Assert.Equal("all", request.Payload.GetProperty("state").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersPaginatesFallbackLookupUntilItFindsTheMatchingSha()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_sha = "wanted-sha",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHeadPages = new Dictionary<string, object[][]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        [
                            new
                            {
                                number = 11111,
                                head = new
                                {
                                    @ref = "conditional-test-runs",
                                    sha = "old-sha",
                                    repo = new
                                    {
                                        owner = new
                                        {
                                            login = "radical"
                                        }
                                    }
                                }
                            }
                        ],
                        [
                            new
                            {
                                number = 13832,
                                head = new
                                {
                                    @ref = "conditional-test-runs",
                                    sha = "wanted-sha",
                                    repo = new
                                    {
                                        owner = new
                                        {
                                            login = "radical"
                                        }
                                    }
                                }
                            }
                        ]
                    ]
                }
            });

        Assert.Equal([13832], result.PullRequestNumbers);
        Assert.Equal(2, result.Requests.Length);
        Assert.Equal(1, result.Requests[0].Payload.GetProperty("page").GetInt32());
        Assert.Equal(2, result.Requests[1].Payload.GetProperty("page").GetInt32());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersRejectsAmbiguousFallbackMatches()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                pullRequestsByHead = new Dictionary<string, object[]>
                {
                    ["radical:conditional-test-runs"] =
                    [
                        new
                        {
                            number = 13832,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "first-sha",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        },
                        new
                        {
                            number = 15177,
                            head = new
                            {
                                @ref = "conditional-test-runs",
                                sha = "second-sha",
                                repo = new
                                {
                                    owner = new
                                    {
                                        login = "radical"
                                    }
                                }
                            }
                        }
                    ]
                }
            });

        Assert.Empty(result.PullRequestNumbers);
        Assert.Single(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task GetAssociatedPullRequestNumbersReturnsEmptyWhenFallbackLookupFails()
    {
        AssociatedPullRequestNumbersResult result = await InvokeHarnessAsync<AssociatedPullRequestNumbersResult>(
            "getAssociatedPullRequestNumbers",
            new
            {
                workflowRun = new
                {
                    head_branch = "conditional-test-runs",
                    head_repository = new
                    {
                        owner = new
                        {
                            login = "radical"
                        }
                    }
                },
                failPullRequestLookup = "HTTP 502"
            });

        Assert.Empty(result.PullRequestNumbers);
        Assert.Single(result.Requests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ComputeRerunEligibilityStillReportsSafeCandidatesDuringDryRun()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                dryRun = true,
                retryableCount = 1
            });

        Assert.True(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ComputeRerunExecutionEligibilityDisablesRerunsWhenDryRunIsEnabled()
    {
        bool rerunExecutionEligible = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new
            {
                dryRun = true,
                retryableCount = 1
            });

        Assert.False(rerunExecutionEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunRequiresAtLeastOneRetryableJob()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 0
            });

        Assert.False(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunIsEligibleWhenRetryableJobsStayWithinTheCap()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 2
            });

        Assert.True(rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AutomaticRerunIsSuppressedWhenMatchedJobsExceedTheCap()
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount = 6
            });

        Assert.False(rerunEligible);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(5, 1, true)]
    [InlineData(5, 2, false)]
    [InlineData(4, 2, true)]
    [InlineData(5, 3, false)]
    [InlineData(1, 4, false)]
    public async Task AttemptSpecificEligibilityRulesAreApplied(int retryableCount, int runAttempt, bool expectedEligible)
    {
        bool rerunEligible = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new
            {
                retryableCount,
                runAttempt
            });

        Assert.Equal(expectedEligible, rerunEligible);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExecutionEligibilityAppliesTheStricterCapAfterTheFirstAttempt()
    {
        bool rerunExecutionEligible = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new
            {
                dryRun = false,
                retryableCount = 5,
                runAttempt = 2
            });

        Assert.False(rerunExecutionEligible);
    }

    // TEMPORARY — FORCE_RERUN_ALL (remove with the force-mode plumbing):
    // Force mode is a short-circuit. It does NOT enumerate or classify jobs, so these
    // tests cover the only force-mode logic that lives in the JS: the eligibility
    // decision (attempt cap only) and the rerun execution (rerun fires with an empty
    // job list, simple comment, open-PR gate preserved).
    [Fact]
    [RequiresTools(["node"])]
    public async Task ForceModeEligibilityDependsOnlyOnTheAttemptCap()
    {
        // Force mode bypasses the job-count cap entirely: eligibility does not depend on
        // retryableCount (the short-circuit never supplies one). It stays true through the
        // attempt cap and flips to false only once the attempt cap is exceeded.
        bool eligibleAttempt1 = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new { runAttempt = 1, forceRerunAll = true });
        Assert.True(eligibleAttempt1);

        bool eligibleAttempt3 = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new { runAttempt = 3, maxRunAttempt = 3, forceRerunAll = true });
        Assert.True(eligibleAttempt3);

        bool eligibleAttempt4 = await InvokeHarnessAsync<bool>(
            "computeRerunEligibility",
            new { runAttempt = 4, maxRunAttempt = 3, forceRerunAll = true });
        Assert.False(eligibleAttempt4);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ForceModeExecutionEligibilityRespectsDryRunAndTheAttemptCap()
    {
        bool executeAttempt1 = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new { dryRun = false, runAttempt = 1, forceRerunAll = true });
        Assert.True(executeAttempt1);

        bool dryRunBlocks = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new { dryRun = true, runAttempt = 1, forceRerunAll = true });
        Assert.False(dryRunBlocks);

        bool attemptCapBlocks = await InvokeHarnessAsync<bool>(
            "computeRerunExecutionEligibility",
            new { dryRun = false, runAttempt = 4, maxRunAttempt = 3, forceRerunAll = true });
        Assert.False(attemptCapBlocks);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PullRequestAndMainRerunsUseDistinctAttemptCaps()
    {
        int pullRequestMaxRunAttempt = await InvokeHarnessAsync<int>("getDefaultMaxRunAttempt", new { });
        int mainMaxRunAttempt = await InvokeHarnessAsync<int>("getMainMaxRunAttempt", new { });

        Assert.Equal(3, pullRequestMaxRunAttempt);
        Assert.Equal(1, mainMaxRunAttempt);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ForceModeRerunsFailedJobsWithAnEmptyJobListWhenPullRequestIsOpen()
    {
        // Force mode passes an empty retryableJobs list (no enumeration). rerunMatchedJobs
        // must still POST the run-level rerun, post a short comment with no job list, and
        // write a summary with no "Retryable jobs" table.
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = Array.Empty<RetryableJobInput>(),
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "open"
                },
                commentHtmlUrlByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "https://github.com/microsoft/aspire/pull/15110#issuecomment-123"
                },
                currentRunResponses = new[]
                {
                    new { run_attempt = 1 },
                    new { run_attempt = 2 }
                },
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                forceRerunAll = true
            });

        Assert.Contains(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");

        RequestRecord commentRequest = Assert.Single(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/issues/{issue_number}/comments");
        string commentBody = commentRequest.Payload.GetProperty("body").GetString()!;
        Assert.Equal(
            "Retrying the failed CI jobs for this pull request from [the CI run attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/1). The rerun is being tracked in [the rerun attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/2).",
            commentBody);

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun requested", headingEvent.Text);

        // No retry-safe job table is written in force mode (no jobs were enumerated).
        Assert.DoesNotContain(result.Events, e => e.Type == "heading" && e.Level == 2 && e.Text == "Retryable jobs");
        Assert.DoesNotContain(result.Events, e => e.Type == "table");
    }

    [Fact]
    public async Task RepresentativeWorkflowFixturesStayAlignedWithCurrentWorkflowDefinitions()
    {
        Dictionary<string, string[]> expectations = new()
        {
            [".github/workflows/run-tests.yml"] =
            [
                "- name: Checkout code",
                "- name: Set up .NET Core",
                "- name: Install sdk for nuget based testing",
                "- name: Build test project",
                "- name: Run tests (Windows)",
                "- name: Upload logs, and test results",
                "- name: Copy CLI E2E recordings for upload",
                "- name: Upload CLI E2E recordings",
                "- name: Generate test results summary",
            ],
            [".github/workflows/build-packages.yml"] =
            [
                "- name: Build with packages",
            ],
            [".github/workflows/polyglot-validation.yml"] =
            [
                "- name: Build Python validation image",
                "- name: Run TypeScript SDK validation",
            ],
            [".github/workflows/ci.yml"] =
            [
                "name: Final Results",
            ],
            [".github/workflows/tests.yml"] =
            [
                "- uses: ./.github/actions/enumerate-tests",
                "name: Final Test Results",
            ],
        };

        foreach ((string relativePath, string[] expectedLines) in expectations)
        {
            string workflowText = await ReadRepoFileAsync(relativePath);

            foreach (string expectedLine in expectedLines)
            {
                Assert.Contains(expectedLine, workflowText);
            }
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task WriteAnalysisSummaryUsesExplicitOutcomeHeadingAndClickableAnalyzedRunLink()
    {
        SummaryResult result = await InvokeHarnessAsync<SummaryResult>(
            "writeAnalysisSummary",
            new
            {
                failedJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                retryableJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                skippedJobs = Array.Empty<SummaryJob>(),
                dryRun = false,
                rerunEligible = true,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun eligible", headingEvent.Text);

        SummaryEvent linkEvent = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 1");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/1", linkEvent.Href);

        SummaryEvent rawEvent = Assert.Single(result.Events, e => e.Type == "raw" && e.Text == "Matched 1 retry-safe job for rerun.");
        Assert.Equal("Matched 1 retry-safe job for rerun.", rawEvent.Text);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task WriteAnalysisSummaryShowsWouldRerunDuringDryRun()
    {
        SummaryResult result = await InvokeHarnessAsync<SummaryResult>(
            "writeAnalysisSummary",
            new
            {
                failedJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                retryableJobs = new[]
                {
                    new SummaryJob { Id = 11, Name = "Tests / One", Reason = "Reason one" }
                },
                skippedJobs = Array.Empty<SummaryJob>(),
                dryRun = true,
                rerunEligible = true,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun eligible", headingEvent.Text);

        SummaryEvent rawEvent = Assert.Single(
            result.Events,
            e => e.Type == "raw" && e.Text == "Matched 1 retry-safe job that would be rerun if dry run were disabled.");
        Assert.Equal("Matched 1 retry-safe job that would be rerun if dry run were disabled.", rawEvent.Text);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsMakesNoRequestsWhenNoJobsAreSupplied()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = Array.Empty<RetryableJobInput>(),
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Empty(result.Requests);
        Assert.Empty(result.Events);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsRequestsOneRunLevelRerunAndWritesTheSummary()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    },
                    new RetryableJobInput
                    {
                        Id = 22,
                        Name = "Tests / Two",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/22",
                        Reason = "Reason two"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "open"
                },
                commentHtmlUrlByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "https://github.com/microsoft/aspire/pull/15110#issuecomment-123"
                },
                currentRunResponses = new[]
                {
                    new { run_attempt = 1 },
                    new { run_attempt = 2 }
                },
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Collection(
            result.Requests,
            request =>
            {
                Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", request.Route);
                Assert.Equal("dotnet", request.Payload.GetProperty("owner").GetString());
                Assert.Equal("aspire", request.Payload.GetProperty("repo").GetString());
                Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());
            },
            request =>
            {
                Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}", request.Route);
                Assert.Equal(123, request.Payload.GetProperty("run_id").GetInt32());
            },
            request =>
            {
                Assert.Equal("POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs", request.Route);
                Assert.Equal(123, request.Payload.GetProperty("run_id").GetInt32());
            },
            request =>
            {
                Assert.Equal("GET /repos/{owner}/{repo}/actions/runs/{run_id}", request.Route);
                Assert.Equal(123, request.Payload.GetProperty("run_id").GetInt32());
            },
            request =>
            {
                Assert.Equal("POST /repos/{owner}/{repo}/issues/{issue_number}/comments", request.Route);
                Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());
                Assert.Equal(
                    "Re-running the failed jobs in the CI workflow for this pull request because 2 jobs were identified as retry-safe transient failures in [the CI run attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/1).\nGitHub was asked to rerun all failed jobs for that attempt, and the rerun is being tracked in [the rerun attempt](https://github.com/microsoft/aspire/actions/runs/123/attempts/2).\nThe job links below point to the failed attempt jobs that matched the retry-safe transient failure rules.\n\n- [Tests / One](https://github.com/microsoft/aspire/actions/runs/123/job/11) - Reason one\n- [Tests / Two](https://github.com/microsoft/aspire/actions/runs/123/job/22) - Reason two",
                    request.Payload.GetProperty("body").GetString());
            });

        SummaryEvent headingEvent = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 1);
        Assert.Equal("Rerun requested", headingEvent.Text);

        SummaryEvent failedAttemptLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 1");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/1", failedAttemptLink.Href);

        SummaryEvent rerunAttemptLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run attempt 2");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123/attempts/2", rerunAttemptLink.Href);

        SummaryEvent commentLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "PR #15110 comment");
        Assert.Equal("https://github.com/microsoft/aspire/pull/15110#issuecomment-123", commentLink.Href);

        SummaryEvent rerunExplanation = Assert.Single(result.Events, e => e.Type == "raw" && e.Text == "The matched jobs below made the run eligible for rerun. GitHub was asked to rerun all failed jobs for the failed attempt.");
        Assert.Equal("The matched jobs below made the run eligible for rerun. GitHub was asked to rerun all failed jobs for the failed attempt.", rerunExplanation.Text);

        SummaryEvent retryableJobsHeading = Assert.Single(result.Events, e => e.Type == "heading" && e.Level == 2 && e.Text == "Retryable jobs");
        Assert.Equal("Retryable jobs", retryableJobsHeading.Text);

        SummaryEvent tableEvent = Assert.Single(result.Events, e => e.Type == "table");
        Assert.Equal("Job", tableEvent.Rows[0][0].GetProperty("data").GetString());
        Assert.Equal("Reason", tableEvent.Rows[0][1].GetProperty("data").GetString());
        Assert.Equal("Tests / One", tableEvent.Rows[1][0].GetString());
        Assert.Equal("Reason one", tableEvent.Rows[1][1].GetString());
        Assert.Equal("Tests / Two", tableEvent.Rows[2][0].GetString());
        Assert.Equal("Reason two", tableEvent.Rows[2][1].GetString());

        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "Pull request comments:");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsSkipsRerunsWhenAllAssociatedPullRequestsAreClosed()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "closed"
                },
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        RequestRecord request = Assert.Single(result.Requests);
        Assert.Equal("GET /repos/{owner}/{repo}/issues/{issue_number}", request.Route);
        Assert.Equal(15110, request.Payload.GetProperty("issue_number").GetInt32());

        SummaryEvent skippedHeading = Assert.Single(result.Events, e => e.Type == "heading" && e.Text == "Rerun skipped");
        Assert.Equal(1, skippedHeading.Level);

        SummaryEvent analyzedRunLink = Assert.Single(result.Events, e => e.Type == "link" && e.Text == "workflow run");
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", analyzedRunLink.Href);

        SummaryEvent skippedRaw = Assert.Single(result.Events, e => e.Type == "raw" && e.Text is not null && e.Text.Contains("All associated pull requests are closed."));
        Assert.Contains("All associated pull requests are closed. No jobs were rerun.", skippedRaw.Text);
    }

    // FORCE_RERUN_ALL: force mode must NOT bypass the open-PR gate. A closed associated
    // PR is still skipped (no value in spending CI on a closed/merged PR). If the gate
    // is ever re-bypassed in force mode, this test fails because the rerun POST fires.
    [Fact]
    [RequiresTools(["node"])]
    public async Task ForceRerunAllStillSkipsRerunWhenAllAssociatedPullRequestsAreClosed()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = Array.Empty<RetryableJobInput>(),
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "closed"
                },
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                forceRerunAll = true
            });

        SummaryEvent skippedHeading = Assert.Single(result.Events, e => e.Type == "heading" && e.Text == "Rerun skipped");
        Assert.Equal(1, skippedHeading.Level);

        SummaryEvent skippedRaw = Assert.Single(result.Events, e => e.Type == "raw" && e.Text is not null && e.Text.Contains("All associated pull requests are closed."));
        Assert.Contains("All associated pull requests are closed. No jobs were rerun.", skippedRaw.Text);

        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunSkipsWhenMainShaAdvanced()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(("currentMainSha", "newer-sha")));

        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Equal("not-requested", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("main-sha-changed", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunSkipsWhenANewerMainCiRunExistsEvenIfSourceIsMissing()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("mainWorkflowRuns", new[] { CreateCurrentMainWorkflowRun(id: 124, runNumber: 101) })));

        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Equal("superseded", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRejectsInvalidOrMissingTrustedSourceRunList()
    {
        (object? MainWorkflowRuns, int ExpectedRequests)[] invalidRunLists =
        [
            (null, 4),
            (Array.Empty<object>(), 4),
            (new[] { new { id = 122, run_number = 99 } }, 4),
            (new[] { new { id = 123, run_number = 99 } }, 4),
            (new[] { new { id = 122, run_number = 100 } }, 4),
            (new { id = 123, run_number = 100 }, 1),
            ("invalid", 1),
            (new[] { new { id = "123", run_number = 100 } }, 1),
            (new[] { new { id = 123, run_number = "100" } }, 1),
        ];

        foreach ((object? mainWorkflowRuns, int expectedRequests) in invalidRunLists)
        {
            RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
                "rerunMatchedJobs",
                CreateCurrentMainRerunInput(("mainWorkflowRuns", mainWorkflowRuns)));

            Assert.DoesNotContain(
                result.Requests,
                r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
            Assert.Equal(
                expectedRequests,
                result.Requests.Count(
                    r => r.Route == "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs"));
            Assert.Equal("not-requested", result.ReturnValue.GetProperty("outcome").GetString());
            Assert.Equal("invalid-main-run-list", result.ReturnValue.GetProperty("reason").GetString());
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunSkipsWhenRunAttemptAdvanced()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("currentRunResponses", new[] { CreateCurrentMainRun(), CreateCurrentMainRun(runAttempt: 2) })));

        Assert.Equal("attempt-changed", result.ReturnValue.GetProperty("reason").GetString());
        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunSkipsWhenAttemptCapIsExceeded()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("sourceRunAttempt", 2),
                ("currentRun", CreateCurrentMainRun(runAttempt: 2))));

        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Equal("attempt-cap-reached", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRequestsFailedJobsOnceWhenLiveStateIsCurrent()
    {
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput());

        Assert.Single(
            result.Requests,
            r => r.Route == rerunRoute);
        Assert.Equal(rerunRoute, result.Requests[^1].Route);
        Assert.Equal("rerun", result.ReturnValue.GetProperty("decision").GetString());
        Assert.Equal("requested", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("eligible", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRechecksMainShaImmediatelyBeforeRequest()
    {
        const string mainRefRoute = "GET /repos/{owner}/{repo}/git/ref/{ref}";
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("currentMainShas", new[] { "main-sha", "new-main-sha" })));

        Assert.Equal(2, result.Requests.Count(r => r.Route == mainRefRoute));
        Assert.DoesNotContain(result.Requests, r => r.Route == rerunRoute);
        Assert.Equal("not-requested", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("main-sha-changed", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRechecksExactRunAfterRunListPolling()
    {
        const string currentRunRoute = "GET /repos/{owner}/{repo}/actions/runs/{run_id}";
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("currentRunResponses", new[] { CreateCurrentMainRun(), CreateCurrentMainRun(runAttempt: 2) }),
                ("mainWorkflowRunResponses", new object?[]
                {
                    Array.Empty<object>(),
                    new[] { CreateCurrentMainWorkflowRun() },
                })));

        Assert.Equal(2, result.Requests.Count(r => r.Route == currentRunRoute));
        Assert.DoesNotContain(result.Requests, r => r.Route == rerunRoute);
        Assert.Equal("not-requested", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("attempt-changed", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("completed", "success")]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRequiresCompletedFailure(string status, string? conclusion)
    {
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("currentRun", CreateCurrentMainRun(status: status, conclusion: conclusion))));

        Assert.DoesNotContain(result.Requests, r => r.Route == rerunRoute);
        Assert.Equal("not-requested", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("invalid-live-run", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunRetriesUntilTrustedSourceAppearsInRunList()
    {
        const string runListRoute = "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs";
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("mainWorkflowRunResponses", new object?[]
                {
                    Array.Empty<object>(),
                    Array.Empty<object>(),
                    Array.Empty<object>(),
                    new[] { CreateCurrentMainWorkflowRun() },
                })));

        Assert.Equal(4, result.Requests.Count(r => r.Route == runListRoute));
        Assert.Single(result.Requests, r => r.Route == rerunRoute);
        Assert.Equal("requested", result.ReturnValue.GetProperty("outcome").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunStopsRunListRetriesWhenLaterResponseIsIneligible()
    {
        const string runListRoute = "GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs";
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        object?[][] responses =
        [
            [
                Array.Empty<object>(),
                new[] { new { id = 124, run_number = 101 } },
            ],
            [
                Array.Empty<object>(),
                "invalid",
            ],
        ];
        string[] expectedReasons = ["superseded", "invalid-main-run-list"];

        for (var i = 0; i < responses.Length; i++)
        {
            RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
                "rerunMatchedJobs",
                CreateCurrentMainRerunInput(("mainWorkflowRunResponses", responses[i])));

            Assert.Equal(2, result.Requests.Count(r => r.Route == runListRoute));
            Assert.DoesNotContain(result.Requests, r => r.Route == rerunRoute);
            Assert.Equal(expectedReasons[i], result.ReturnValue.GetProperty("reason").GetString());
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunReturnsFailedOutcomeWhenRequestIsRejected()
    {
        const string rerunRoute = "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs";
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(("failedRequestRoutes", new[] { rerunRoute })));

        Assert.Single(result.Requests, r => r.Route == rerunRoute);
        Assert.Equal("rerun", result.ReturnValue.GetProperty("decision").GetString());
        Assert.Equal("failed", result.ReturnValue.GetProperty("outcome").GetString());
        Assert.Equal("request-failed", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CurrentMainRerunSkipsWhenLiveRunShaDoesNotMatchTrustedSource()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            CreateCurrentMainRerunInput(
                ("sourceHeadSha", "trusted-main-sha"),
                ("currentRun", CreateCurrentMainRun(headSha: "different-sha")),
                ("currentMainSha", "trusted-main-sha")));

        Assert.DoesNotContain(
            result.Requests,
            r => r.Route == "POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs");
        Assert.Equal("invalid-live-run", result.ReturnValue.GetProperty("reason").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunMatchedJobsDoesNotFabricateCommentLinksWhenGitHubDoesNotReturnACommentUrl()
    {
        RerunMatchedJobsResult result = await InvokeHarnessAsync<RerunMatchedJobsResult>(
            "rerunMatchedJobs",
            new
            {
                owner = "dotnet",
                repo = "aspire",
                retryableJobs = new[]
                {
                    new RetryableJobInput
                    {
                        Id = 11,
                        Name = "Tests / One",
                        HtmlUrl = "https://github.com/microsoft/aspire/actions/runs/123/job/11",
                        Reason = "Reason one"
                    }
                },
                pullRequestNumbers = new[] { 15110 },
                issueStatesByNumber = new Dictionary<string, string>
                {
                    ["15110"] = "open"
                },
                currentRunResponses = new[]
                {
                    new { run_attempt = 1 },
                    new { run_attempt = 2 }
                },
                sourceRunId = 123,
                sourceRunAttempt = 1,
                sourceRunUrl = "https://github.com/microsoft/aspire/actions/runs/123"
            });

        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "Pull request comments:");
        Assert.DoesNotContain(result.Events, e => e.Type == "link" && e.Text == "PR #15110 comment");
        Assert.Contains(result.Events, e => e.Type == "raw" && e.Text == "PR #15110 comment");
    }

    // --- Test retry pattern config validation tests ---

    [Fact]
    public async Task TestRetryPatternsJsonIsValidAndWellFormed()
    {
        string configJson = await ReadRepoFileAsync("eng/test-retry-patterns.json");

        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("version", out JsonElement version));
        Assert.Equal(1, version.GetInt32());
        Assert.True(root.TryGetProperty("testFailurePatterns", out JsonElement testPatterns));
        Assert.Equal(JsonValueKind.Array, testPatterns.ValueKind);
        Assert.True(root.TryGetProperty("jobFailurePatterns", out JsonElement jobPatterns));
        Assert.Equal(JsonValueKind.Array, jobPatterns.ValueKind);

        // Validate no unknown top-level properties
        HashSet<string> allowedTopLevel = ["version", "testFailurePatterns", "jobFailurePatterns"];
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            Assert.Contains(prop.Name, allowedTopLevel);
        }
    }

    [Fact]
    public async Task TestRetryPatternsJsonHasValidRuleStructure()
    {
        string configJson = await ReadRepoFileAsync("eng/test-retry-patterns.json");

        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        HashSet<string> testPatternAllowedFields = ["testName", "testProject", "output", "reason", "enabled"];
        HashSet<string> jobPatternAllowedFields = ["jobName", "output", "reason", "enabled"];
        HashSet<string> testPatternMatcherFields = ["testName", "testProject", "output"];
        HashSet<string> jobPatternMatcherFields = ["jobName", "output"];

        foreach (JsonElement rule in root.GetProperty("testFailurePatterns").EnumerateArray())
        {
            ValidatePatternRule(rule, testPatternAllowedFields, testPatternMatcherFields);
        }

        foreach (JsonElement rule in root.GetProperty("jobFailurePatterns").EnumerateArray())
        {
            ValidatePatternRule(rule, jobPatternAllowedFields, jobPatternMatcherFields);
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TestRetryPatternsJsonPassesJavaScriptValidation()
    {
        string configPath = Path.Combine(_repoRoot, "eng", "test-retry-patterns.json");

        LoadRetryPatternsConfigResult result = await InvokeHarnessAsync<LoadRetryPatternsConfigResult>(
            "validateRetryPatternsConfigFromFile",
            new { configPath });

        Assert.NotNull(result.Config);
        Assert.Empty(result.Errors);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TestRetryPatternsJsonRegexesCompileInJavaScript()
    {
        string configJson = await ReadRepoFileAsync("eng/test-retry-patterns.json");
        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        List<string> regexPatterns = [];
        ExtractRegexPatterns(root.GetProperty("testFailurePatterns"), regexPatterns);
        ExtractRegexPatterns(root.GetProperty("jobFailurePatterns"), regexPatterns);

        foreach (string pattern in regexPatterns)
        {
            bool matches = await InvokeHarnessAsync<bool>(
                "matchesRetryPattern",
                new { text = "test-input", patternValue = new { regex = pattern } });

            // We don't care about the result, just that it didn't throw.
            // The harness would fail if the regex was invalid.
            Assert.IsType<bool>(matches);
        }
    }

    // --- Pattern matching function tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesRetryPatternMatchesSubstringCaseInsensitively()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "matchesRetryPattern",
            new { text = "Error: ECONNRESET detected", patternValue = "econnreset" });

        Assert.True(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesRetryPatternReturnsFalseWhenSubstringNotPresent()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "matchesRetryPattern",
            new { text = "Some other error", patternValue = "ECONNRESET" });

        Assert.False(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesRetryPatternMatchesRegexCaseInsensitively()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "matchesRetryPattern",
            new { text = "Aspire.Hosting.Redis.Tests.ConnectionTest", patternValue = new { regex = ".*redis.*" } });

        Assert.True(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesRetryPatternReturnsFalseForNonMatchingRegex()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "matchesRetryPattern",
            new { text = "Aspire.Hosting.Kafka.Tests", patternValue = new { regex = "^Redis" } });

        Assert.False(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchesRetryPatternHandlesInvalidRegexGracefully()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "matchesRetryPattern",
            new { text = "some text", patternValue = new { regex = "[invalid" } });

        Assert.False(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsReturnsShouldRetryWhenOutputMatches()
    {
        var failedTests = new[]
        {
            new { testName = "Aspire.Redis.Tests.ConnectAsync", output = "Error: ECONNRESET: connection reset" }
        };
        var patterns = new[]
        {
            new { output = "ECONNRESET", reason = "Transient network reset", enabled = true }
        };

        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests, testProject = "Aspire.Redis.Tests", patterns });

        Assert.True(result.ShouldRetry);
        Assert.Single(result.MatchedTests);
        Assert.Equal("Transient network reset", result.MatchedTests[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsReturnsFalseWhenNoPatternMatches()
    {
        var failedTests = new[]
        {
            new { testName = "Aspire.Tests.SomeTest", output = "Assertion failed: expected true" }
        };
        var patterns = new[]
        {
            new { output = "ECONNRESET", reason = "Transient network reset", enabled = true }
        };

        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests, testProject = "Aspire.Tests", patterns });

        Assert.False(result.ShouldRetry);
        Assert.Empty(result.MatchedTests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsSkipsDisabledPatterns()
    {
        var failedTests = new[]
        {
            new { testName = "Aspire.Tests.Flaky", output = "ECONNRESET" }
        };
        var patterns = new object[]
        {
            new { output = "ECONNRESET", reason = "Disabled pattern", enabled = false },
            new { output = "ECONNREFUSED", reason = "Enabled pattern", enabled = true }
        };

        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests, testProject = "Aspire.Tests", patterns });

        Assert.False(result.ShouldRetry);
        Assert.Empty(result.MatchedTests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsUsesAndLogicWithinRules()
    {
        var failedTests = new[]
        {
            new { testName = "Aspire.Kafka.Tests.ProducerTest", output = "ECONNRESET" }
        };
        var patterns = new object[]
        {
            new { testName = new { regex = ".*Redis.*" }, output = "ECONNRESET", reason = "Redis ECONNRESET" }
        };

        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests, testProject = "Aspire.Kafka.Tests", patterns });

        // testName doesn't match (Kafka, not Redis), so AND fails
        Assert.False(result.ShouldRetry);
        Assert.Empty(result.MatchedTests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsUsesOrLogicAcrossRules()
    {
        var failedTests = new[]
        {
            new { testName = "Aspire.Tests.Timeout", output = "Operation timed out" }
        };
        var patterns = new object[]
        {
            new { output = "ECONNRESET", reason = "Network reset" },
            new { output = "timed out", reason = "Timeout" }
        };

        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests, testProject = "Aspire.Tests", patterns });

        Assert.True(result.ShouldRetry);
        Assert.Single(result.MatchedTests);
        Assert.Equal("Timeout", result.MatchedTests[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchTestFailurePatternsReturnsEmptyForNullInputs()
    {
        MatchTestFailurePatternsResult result = await InvokeHarnessAsync<MatchTestFailurePatternsResult>(
            "matchTestFailurePatterns",
            new { failedTests = Array.Empty<object>(), testProject = "", patterns = Array.Empty<object>() });

        Assert.False(result.ShouldRetry);
        Assert.Empty(result.MatchedTests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchJobLogPatternMatchesJobNameAndOutput()
    {
        var patterns = new object[]
        {
            new { jobName = new { regex = ".*windows.*" }, output = "0xC0000142", reason = "Windows init failure" }
        };

        MatchJobLogPatternResult? result = await InvokeHarnessAsync<MatchJobLogPatternResult?>(
            "matchJobLogPattern",
            new { jobName = "Tests / Build (windows-latest)", jobLogText = "Process failed with 0xC0000142", patterns });

        Assert.NotNull(result);
        Assert.True(result.Matched);
        Assert.Equal("Windows init failure", result.Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchJobLogPatternReturnsNullWhenJobNameDoesNotMatch()
    {
        var patterns = new object[]
        {
            new { jobName = new { regex = ".*windows.*" }, output = "0xC0000142", reason = "Windows init failure" }
        };

        MatchJobLogPatternResult? result = await InvokeHarnessAsync<MatchJobLogPatternResult?>(
            "matchJobLogPattern",
            new { jobName = "Tests / Build (ubuntu-latest)", jobLogText = "Process failed with 0xC0000142", patterns });

        Assert.Null(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MatchJobLogPatternMatchesOutputOnlyPatterns()
    {
        var patterns = new object[]
        {
            new { output = "Could not resolve host", reason = "DNS failure" }
        };

        MatchJobLogPatternResult? result = await InvokeHarnessAsync<MatchJobLogPatternResult?>(
            "matchJobLogPattern",
            new { jobName = "Tests / Any", jobLogText = "Error: Could not resolve host github.com", patterns });

        Assert.NotNull(result);
        Assert.True(result.Matched);
        Assert.Equal("DNS failure", result.Reason);
    }

    // --- analyzeFailedJobs with retryPatternsConfig tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeFailedJobsWithConfigRetriesTestExecFailureViaJobLogPattern()
    {
        WorkflowJob job = CreateJob(id: 1, failedSteps: ["Run tests"]);

        AnalyzeFailedJobsResult result = await AnalyzeJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = "Process failed with 0xC0000142" },
            retryPatternsConfig: new
            {
                version = 1,
                testFailurePatterns = Array.Empty<object>(),
                jobFailurePatterns = new object[]
                {
                    new { jobName = new { regex = ".*" }, output = "0xC0000142", reason = "Windows init failure" }
                }
            });

        Assert.Single(result.RetryableJobs);
        Assert.Contains("configurable test-retry pattern", result.RetryableJobs[0].Reason);
        Assert.Contains("Windows init failure", result.RetryableJobs[0].Reason);
        Assert.Empty(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeFailedJobsWithConfigSkipsWhenJobLogDoesNotMatchPattern()
    {
        WorkflowJob job = CreateJob(id: 1, failedSteps: ["Run tests"]);

        AnalyzeFailedJobsResult result = await AnalyzeJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = "Some unrelated failure" },
            retryPatternsConfig: new
            {
                version = 1,
                testFailurePatterns = Array.Empty<object>(),
                jobFailurePatterns = new object[]
                {
                    new { output = "0xC0000142", reason = "Windows init failure" }
                }
            });

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeFailedJobsWithConfigDoesNotApplyJobLogPatternToNonTestExecFailures()
    {
        // A job that fails on a non-test step should NOT use jobFailurePatterns
        WorkflowJob job = CreateJob(id: 1, failedSteps: ["Compile project"]);

        AnalyzeFailedJobsResult result = await AnalyzeJobsAsync(
            [job],
            new Dictionary<string, string> { ["1"] = "Process completed with exit code 1." },
            new Dictionary<string, string> { ["1"] = "0xC0000142" },
            retryPatternsConfig: new
            {
                version = 1,
                testFailurePatterns = Array.Empty<object>(),
                jobFailurePatterns = new object[]
                {
                    new { output = "0xC0000142", reason = "Windows init failure" }
                }
            });

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
    }

    // --- analyzeTrxFiles tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeTrxFilesFindsMatchedTests()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.FailingTest", "Failed", ErrorMessage: "ECONNRESET: connection reset"),
            new TrxTestResult("Aspire.Tests.PassingTest", "Passed"));

        var patterns = new object[]
        {
            new { output = "ECONNRESET", reason = "Network reset" }
        };

        AnalyzeTrxFilesResult result = await InvokeHarnessAsync<AnalyzeTrxFilesResult>(
            "analyzeTrxFiles",
            new
            {
                trxFileContents = new[] { new { fileName = "Aspire.Tests.trx", content = trxContent } },
                testFailurePatterns = patterns
            });

        Assert.Single(result.AllMatchedTests);
        Assert.Equal("Aspire.Tests.FailingTest", result.AllMatchedTests[0].TestName);
        Assert.Equal("Network reset", result.AllMatchedTests[0].Reason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeTrxFilesDedupesByTestName()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.Flaky", "Failed", ErrorMessage: "ECONNRESET"));

        var patterns = new object[]
        {
            new { output = "ECONNRESET", reason = "Network reset" }
        };

        // Same test in two TRX files
        AnalyzeTrxFilesResult result = await InvokeHarnessAsync<AnalyzeTrxFilesResult>(
            "analyzeTrxFiles",
            new
            {
                trxFileContents = new[]
                {
                    new { fileName = "Aspire.Tests.trx", content = trxContent },
                    new { fileName = "Aspire.Tests.retry.trx", content = trxContent }
                },
                testFailurePatterns = patterns
            });

        Assert.Single(result.AllMatchedTests);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AnalyzeTrxFilesReturnsEmptyWhenNoMatches()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.LogicError", "Failed", ErrorMessage: "Assert.True() Failure"));

        var patterns = new object[]
        {
            new { output = "ECONNRESET", reason = "Network reset" }
        };

        AnalyzeTrxFilesResult result = await InvokeHarnessAsync<AnalyzeTrxFilesResult>(
            "analyzeTrxFiles",
            new
            {
                trxFileContents = new[] { new { fileName = "Aspire.Tests.trx", content = trxContent } },
                testFailurePatterns = patterns
            });

        Assert.Empty(result.AllMatchedTests);
    }

    // --- promoteTestExecutionFailureJobs tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task PromoteTestExecutionFailureJobsMovesTestExecJobsToRetryable()
    {
        var skippedJobs = new[]
        {
            new { id = 1, name = "Tests / Build (ubuntu-latest)", htmlUrl = (string?)null, failedSteps = new[] { "Run tests" }, reason = "Not retryable." },
            new { id = 2, name = "Build / Compile", htmlUrl = (string?)null, failedSteps = new[] { "Compile project" }, reason = "Not retryable." }
        };
        var allMatchedTests = new[]
        {
            new { testName = "Aspire.Tests.Flaky", reason = "Network reset", testProject = "Aspire.Tests" }
        };

        PromoteTestExecutionFailureJobsResult result = await InvokeHarnessAsync<PromoteTestExecutionFailureJobsResult>(
            "promoteTestExecutionFailureJobs",
            new { retryableJobs = Array.Empty<object>(), skippedJobs, allMatchedTests });

        Assert.Single(result.RetryableJobs);
        Assert.Equal("Tests / Build (ubuntu-latest)", result.RetryableJobs[0].Name);
        Assert.Contains("transient test failure patterns", result.RetryableJobs[0].Reason);

        // Non-test-exec job stays skipped
        Assert.Single(result.SkippedJobs);
        Assert.Equal("Build / Compile", result.SkippedJobs[0].Name);

        Assert.Single(result.PromotedJobs);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PromoteTestExecutionFailureJobsDoesNothingWhenNoMatchedTests()
    {
        var skippedJobs = new[]
        {
            new { id = 1, name = "Tests / Build (ubuntu-latest)", htmlUrl = (string?)null, failedSteps = new[] { "Run tests" }, reason = "Not retryable." }
        };

        PromoteTestExecutionFailureJobsResult result = await InvokeHarnessAsync<PromoteTestExecutionFailureJobsResult>(
            "promoteTestExecutionFailureJobs",
            new { retryableJobs = Array.Empty<object>(), skippedJobs, allMatchedTests = Array.Empty<object>() });

        Assert.Empty(result.RetryableJobs);
        Assert.Single(result.SkippedJobs);
        Assert.Empty(result.PromotedJobs);
    }

    // --- selectTestResultsArtifact tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectTestResultsArtifactReturnsNewestNonExpired()
    {
        var artifacts = new object[]
        {
            new { name = "All-TestResults", size_in_bytes = 1000, expired = false, created_at = "2024-01-01T00:00:00Z", id = 1 },
            new { name = "All-TestResults", size_in_bytes = 2000, expired = false, created_at = "2024-01-02T00:00:00Z", id = 2 },
            new { name = "Other-Artifact", size_in_bytes = 500, expired = false, created_at = "2024-01-03T00:00:00Z", id = 3 }
        };

        SelectTestResultsArtifactResult? result = await InvokeHarnessAsync<SelectTestResultsArtifactResult?>(
            "selectTestResultsArtifact",
            new { artifacts });

        Assert.NotNull(result);
        Assert.Equal(2, result.Id);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectTestResultsArtifactReturnsNullForExpired()
    {
        var artifacts = new object[]
        {
            new { name = "All-TestResults", size_in_bytes = 1000, expired = true, created_at = "2024-01-01T00:00:00Z", id = 1 }
        };

        SelectTestResultsArtifactResult? result = await InvokeHarnessAsync<SelectTestResultsArtifactResult?>(
            "selectTestResultsArtifact",
            new { artifacts });

        Assert.Null(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectTestResultsArtifactReturnsNullForOversized()
    {
        var artifacts = new object[]
        {
            new { name = "All-TestResults", size_in_bytes = 200 * 1024 * 1024, expired = false, created_at = "2024-01-01T00:00:00Z", id = 1 }
        };

        SelectTestResultsArtifactResult? result = await InvokeHarnessAsync<SelectTestResultsArtifactResult?>(
            "selectTestResultsArtifact",
            new { artifacts });

        Assert.Null(result);
    }

    // --- hasTestExecutionFailureStep tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task HasTestExecutionFailureStepReturnsTrueForRunTests()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "hasTestExecutionFailureStep",
            new { failedSteps = new[] { "Run tests (ubuntu-latest)" } });

        Assert.True(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task HasTestExecutionFailureStepReturnsFalseForNonTestSteps()
    {
        bool result = await InvokeHarnessAsync<bool>(
            "hasTestExecutionFailureStep",
            new { failedSteps = new[] { "Checkout code" } });

        Assert.False(result);
    }

    // --- TRX parsing tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxExtractsFailedTests()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.PassingTest", "Passed"),
            new TrxTestResult("Aspire.Tests.FailingTest", "Failed", ErrorMessage: "ECONNRESET", StackTrace: "at Line 42"));

        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent });

        Assert.Single(results);
        Assert.Equal("Aspire.Tests.FailingTest", results[0].TestName);
        Assert.Contains("ECONNRESET", results[0].Output);
        Assert.Contains("at Line 42", results[0].Output);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxReturnsEmptyForNoFailures()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.PassingTest", "Passed"));

        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent });

        Assert.Empty(results);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxReturnsEmptyForEmptyInput()
    {
        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent = "" });

        Assert.Empty(results);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxHandlesMalformedXml()
    {
        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent = "<not valid xml" });

        Assert.Empty(results);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxCapsOutputAt10KB()
    {
        string longMessage = new('x', 12_000);
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.BigOutput", "Failed", ErrorMessage: longMessage));

        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent });

        Assert.Single(results);
        Assert.True(results[0].Output.Length <= 10 * 1024, $"Output length {results[0].Output.Length} exceeds 10KB cap");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxDecodesXmlEntities()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.EntityTest", "Failed", ErrorMessage: "Expected &lt;value&gt; but got &amp;null"));

        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent });

        Assert.Single(results);
        Assert.Contains("<value>", results[0].Output);
        Assert.Contains("&null", results[0].Output);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExtractFailedTestsFromTrxDecodesDoubleEncodedXmlEntities()
    {
        string trxContent = BuildTrxContent(
            new TrxTestResult("Aspire.Tests.QuoteTest", "Failed", ErrorMessage: "Expected &amp;quot;hello&amp;quot; but got &amp;apos;world&amp;apos;"));

        ExtractFailedTestsFromTrxResult[] results = await InvokeHarnessAsync<ExtractFailedTestsFromTrxResult[]>(
            "extractFailedTestsFromTrx",
            new { trxContent });

        Assert.Single(results);
        Assert.Contains("&quot;hello&quot;", results[0].Output);
        Assert.Contains("&apos;world&apos;", results[0].Output);
    }

    // --- Config validation edge case tests ---

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigRejectsUnknownTopLevelProperties()
    {
        var config = new
        {
            version = 1,
            testFailurePatterns = Array.Empty<object>(),
            jobFailurePatterns = Array.Empty<object>(),
            unknownProp = "bad"
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("unknownProp"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigRejectsWrongVersion()
    {
        var config = new
        {
            version = 2,
            testFailurePatterns = Array.Empty<object>(),
            jobFailurePatterns = Array.Empty<object>()
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("version"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigRejectsMissingReason()
    {
        var config = new
        {
            version = 1,
            testFailurePatterns = new object[]
            {
                new { output = "ECONNRESET" }
            },
            jobFailurePatterns = Array.Empty<object>()
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("reason"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigRejectsRuleWithNoMatcherFields()
    {
        var config = new
        {
            version = 1,
            testFailurePatterns = new object[]
            {
                new { reason = "No matchers here" }
            },
            jobFailurePatterns = Array.Empty<object>()
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("matcher field"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigRejectsInvalidRegex()
    {
        var config = new
        {
            version = 1,
            testFailurePatterns = new object[]
            {
                new { testName = new { regex = "[invalid" }, reason = "Bad regex" }
            },
            jobFailurePatterns = Array.Empty<object>()
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("invalid regex") || e.Contains("Invalid"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ValidateRetryPatternsConfigAcceptsValidConfig()
    {
        var config = new
        {
            version = 1,
            testFailurePatterns = new object[]
            {
                new { output = "ECONNRESET", reason = "Network reset" },
                new { testName = new { regex = ".*Redis.*" }, output = "refused", reason = "Redis race" }
            },
            jobFailurePatterns = new object[]
            {
                new { jobName = new { regex = ".*windows.*" }, output = "0xC0000142", reason = "Win init" }
            }
        };

        ValidationResult result = await InvokeHarnessAsync<ValidationResult>(
            "validateRetryPatternsConfig",
            new { config });

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    private static void ValidatePatternRule(JsonElement rule, HashSet<string> allowedFields, HashSet<string> matcherFields)
    {
        Assert.Equal(JsonValueKind.Object, rule.ValueKind);

        foreach (JsonProperty prop in rule.EnumerateObject())
        {
            Assert.Contains(prop.Name, allowedFields);
        }

        Assert.True(
            rule.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String && reason.GetString()!.Length > 0,
            "Each rule must have a non-empty 'reason' string.");

        if (rule.TryGetProperty("enabled", out JsonElement enabled))
        {
            Assert.True(
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "'enabled' must be a boolean.");
        }

        bool hasMatcherField = false;
        foreach (string field in matcherFields)
        {
            if (rule.TryGetProperty(field, out JsonElement fieldValue))
            {
                hasMatcherField = true;
                ValidatePatternValue(fieldValue, $"{field}");
            }
        }

        Assert.True(hasMatcherField, $"Rule must contain at least one matcher field ({string.Join(", ", matcherFields)}).");
    }

    private static void ValidatePatternValue(JsonElement value, string fieldName)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            Assert.True(value.GetString()!.Length > 0, $"{fieldName}: string pattern must be non-empty.");
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            Assert.True(
                value.TryGetProperty("regex", out JsonElement regex) && regex.ValueKind == JsonValueKind.String && regex.GetString()!.Length > 0,
                $"{fieldName}: regex pattern must have a non-empty 'regex' string.");
            return;
        }

        Assert.Fail($"{fieldName}: must be a string or {{ \"regex\": \"...\" }} object.");
    }

    private static void ExtractRegexPatterns(JsonElement patternsArray, List<string> regexPatterns)
    {
        foreach (JsonElement rule in patternsArray.EnumerateArray())
        {
            foreach (JsonProperty prop in rule.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("regex", out JsonElement regex) &&
                    regex.ValueKind == JsonValueKind.String)
                {
                    regexPatterns.Add(regex.GetString()!);
                }
            }
        }
    }

    private static string BuildTrxContent(params TrxTestResult[] results)
    {
        List<string> resultElements = [];

        foreach (TrxTestResult test in results)
        {
            string outputElement = "";

            if (!string.IsNullOrEmpty(test.ErrorMessage) || !string.IsNullOrEmpty(test.StackTrace) || !string.IsNullOrEmpty(test.StdOut))
            {
                string errorInfo = "";
                if (!string.IsNullOrEmpty(test.ErrorMessage) || !string.IsNullOrEmpty(test.StackTrace))
                {
                    errorInfo = "<ErrorInfo>" +
                        (string.IsNullOrEmpty(test.ErrorMessage) ? "" : $"<Message>{test.ErrorMessage}</Message>") +
                        (string.IsNullOrEmpty(test.StackTrace) ? "" : $"<StackTrace>{test.StackTrace}</StackTrace>") +
                        "</ErrorInfo>";
                }

                string stdOut = string.IsNullOrEmpty(test.StdOut) ? "" : $"<StdOut>{test.StdOut}</StdOut>";
                outputElement = $"<Output>{errorInfo}{stdOut}</Output>";
            }

            resultElements.Add(
                $"""<UnitTestResult testName="{test.TestName}" outcome="{test.Outcome}">{outputElement}</UnitTestResult>""");
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun>
              <Results>
                {string.Join("\n    ", resultElements)}
              </Results>
            </TestRun>
            """;
    }

    private sealed record TrxTestResult(
        string TestName,
        string Outcome,
        string ErrorMessage = "",
        string StackTrace = "",
        string StdOut = "");

    private async Task<AnalyzeFailedJobsResult> AnalyzeSingleJobAsync(WorkflowJob job, string annotationsOrText, string jobLogText = "")
    {
        Dictionary<string, string>? jobLogTextByJobId = string.IsNullOrEmpty(jobLogText)
            ? null
            : new Dictionary<string, string> { [job.Id.ToString()] = jobLogText };

        return await AnalyzeJobsAsync(
            [job],
            new Dictionary<string, string> { [job.Id.ToString()] = annotationsOrText },
            jobLogTextByJobId);
    }

    private Task<AnalyzeFailedJobsResult> AnalyzeJobsAsync(
        WorkflowJob[] jobs,
        Dictionary<string, string> annotationTextByJobId,
        Dictionary<string, string>? jobLogTextByJobId = null,
        int? maxRetryableJobs = null,
        object? retryPatternsConfig = null)
        => InvokeHarnessAsync<AnalyzeFailedJobsResult>(
            "analyzeFailedJobs",
            new AnalyzeFailedJobsRequest
            {
                Jobs = jobs,
                AnnotationTextByJobId = annotationTextByJobId,
                JobLogTextByJobId = jobLogTextByJobId,
                MaxRetryableJobs = maxRetryableJobs,
                RetryPatternsConfig = retryPatternsConfig
            });

    private Task<AnalyzeFailedJobsResult> AnalyzeMainJobsAsync(
        WorkflowJob[] jobs,
        Dictionary<string, string> annotationTextByJobId,
        Dictionary<string, string>? jobLogTextByJobId = null,
        int? maxRetryableJobs = null,
        int runAttempt = 1)
        => InvokeHarnessAsync<AnalyzeFailedJobsResult>(
            "analyzeMainFailedJobs",
            new AnalyzeFailedJobsRequest
            {
                Jobs = jobs,
                AnnotationTextByJobId = annotationTextByJobId,
                JobLogTextByJobId = jobLogTextByJobId,
                MaxRetryableJobs = maxRetryableJobs,
                RunAttempt = runAttempt
            });

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        string inputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        string requestJson = JsonSerializer.Serialize(new HarnessRequest
        {
            Operation = operation,
            Payload = payload
        }, s_jsonOptions);

        await File.WriteAllTextAsync(inputPath, requestJson);

        using NodeCommand command = new(_output, label: operation);
        command.WithWorkingDirectory(_repoRoot).WithTimeout(TimeSpan.FromMinutes(1));

        CommandResult result = await command.ExecuteScriptAsync(_harnessPath, inputPath);
        result.EnsureSuccessful();

        HarnessResponse<T>? response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);

        return response.Result!;
    }

    private static WorkflowJob CreateJob(
        int id = 1,
        string name = "Tests / Sample / Sample (ubuntu-latest)",
        string conclusion = "failure",
        string[]? failedSteps = null,
        string[]? successfulSteps = null,
        string[]? skippedSteps = null)
        => new()
        {
            Id = id,
            Name = name,
            Conclusion = conclusion,
            Steps = (failedSteps ?? [])
                .Select(stepName => new WorkflowStep { Name = stepName, Conclusion = "failure" })
                .Concat((successfulSteps ?? [])
                    .Select(stepName => new WorkflowStep { Name = stepName, Conclusion = "success" }))
                .Concat((skippedSteps ?? [])
                    .Select(stepName => new WorkflowStep { Name = stepName, Conclusion = "skipped" }))
                .ToArray()
        };

    private static RetryableJobInput[] CreateMainRetryableJobs() =>
    [
        new()
        {
            Id = 1,
            Name = "Tests / Sample / Sample (ubuntu-latest)",
            Reason = "Matched the current-main transient infrastructure allowlist."
        }
    ];

    private static Dictionary<string, object?> CreateCurrentMainRerunInput(
        params (string Name, object? Value)[] overrides)
    {
        Dictionary<string, object?> input = new()
        {
            ["owner"] = "dotnet",
            ["repo"] = "aspire",
            ["retryableJobs"] = CreateMainRetryableJobs(),
            ["sourceRunId"] = 123,
            ["sourceRunAttempt"] = 1,
            ["sourceRunUrl"] = "https://github.com/microsoft/aspire/actions/runs/123",
            ["sourceRunScope"] = "main",
            ["sourceHeadSha"] = "main-sha",
            ["maxRunAttempt"] = 1,
            ["currentRun"] = CreateCurrentMainRun(),
            ["currentMainSha"] = "main-sha",
            ["mainWorkflowRuns"] = new[] { CreateCurrentMainWorkflowRun() },
        };

        foreach ((string name, object? value) in overrides)
        {
            input[name] = value;
        }

        return input;
    }

    private static object CreateCurrentMainRun(
        int runAttempt = 1,
        string headSha = "main-sha",
        string status = "completed",
        string? conclusion = "failure")
        => new
        {
            id = 123,
            run_attempt = runAttempt,
            run_number = 100,
            workflow_id = 456,
            @event = "push",
            head_branch = "main",
            head_sha = headSha,
            path = ".github/workflows/ci.yml",
            status,
            conclusion,
        };

    private static object CreateCurrentMainWorkflowRun(
        int id = 123,
        int runNumber = 100,
        string headSha = "main-sha")
        => new
        {
            id,
            run_number = runNumber,
            @event = "push",
            head_branch = "main",
            head_sha = headSha,
        };

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class HarnessRequest
    {
        public string Operation { get; init; } = string.Empty;
        public object? Payload { get; init; }
    }

    private sealed class HarnessResponse<T>
    {
        public T? Result { get; init; }
    }

    private sealed class AnalyzeFailedJobsRequest
    {
        public WorkflowJob[] Jobs { get; init; } = [];
        public Dictionary<string, string> AnnotationTextByJobId { get; init; } = [];
        public Dictionary<string, string>? JobLogTextByJobId { get; init; }
        public int? MaxRetryableJobs { get; init; }
        public int RunAttempt { get; init; } = 1;
        public object? RetryPatternsConfig { get; init; }
    }

    private sealed class AnalyzeFailedJobsResult
    {
        public AnalyzedJob[] FailedJobs { get; init; } = [];
        public AnalyzedJob[] RetryableJobs { get; init; } = [];
        public AnalyzedJob[] SkippedJobs { get; init; } = [];
        public bool RerunEligible { get; init; }
        public int[] AnnotationRequestJobIds { get; init; } = [];
        public int[] LogRequestJobIds { get; init; } = [];
    }

    private sealed class AssociatedPullRequestNumbersResult
    {
        public int[] PullRequestNumbers { get; init; } = [];
        public RequestRecord[] Requests { get; init; } = [];
    }

    private sealed class AnalyzedJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? HtmlUrl { get; init; }
        public string[] FailedSteps { get; init; } = [];
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class WorkflowJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Conclusion { get; init; } = string.Empty;
        public WorkflowStep[] Steps { get; init; } = [];

        [JsonPropertyName("check_run_url")]
        public string? CheckRunUrl { get; init; }
    }

    private sealed class WorkflowStep
    {
        public string Name { get; init; } = string.Empty;
        public string Conclusion { get; init; } = string.Empty;
    }

    private sealed class SummaryResult
    {
        public SummaryEvent[] Events { get; init; } = [];
    }

    private sealed class SummaryEvent
    {
        public string Type { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string? Href { get; init; }
        public int? Level { get; init; }
        public bool? AddEol { get; init; }
        public JsonElement[][] Rows { get; init; } = [];
    }

    private sealed class SummaryJob
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class RetryableJobInput
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? HtmlUrl { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class RerunMatchedJobsResult
    {
        public RequestRecord[] Requests { get; init; } = [];
        public SummaryEvent[] Events { get; init; } = [];
        public JsonElement ReturnValue { get; init; }
    }

    private sealed class RequestRecord
    {
        public string Route { get; init; } = string.Empty;
        public JsonElement Payload { get; init; }
    }

    private sealed class LoadRetryPatternsConfigResult
    {
        public JsonElement? Config { get; init; }
        public string[] Errors { get; init; } = [];
    }

    private sealed class ValidationResult
    {
        public bool Valid { get; init; }
        public string[] Errors { get; init; } = [];
    }

    private sealed class MatchTestFailurePatternsResult
    {
        public bool ShouldRetry { get; init; }
        public MatchedTest[] MatchedTests { get; init; } = [];
    }

    private sealed class MatchedTest
    {
        public string TestName { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string? MatchedSnippet { get; init; }
    }

    private sealed class MatchJobLogPatternResult
    {
        public bool Matched { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    private sealed class ExtractFailedTestsFromTrxResult
    {
        public string TestName { get; init; } = string.Empty;
        public string Output { get; init; } = string.Empty;
    }

    private sealed class AnalyzeTrxFilesResult
    {
        public AnalyzedTrxMatch[] AllMatchedTests { get; init; } = [];
    }

    private sealed class AnalyzedTrxMatch
    {
        public string TestName { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public string? MatchedSnippet { get; init; }
        public string TestProject { get; init; } = string.Empty;
    }

    private sealed class PromoteTestExecutionFailureJobsResult
    {
        public AnalyzedJob[] RetryableJobs { get; init; } = [];
        public AnalyzedJob[] SkippedJobs { get; init; } = [];
        public AnalyzedJob[] PromotedJobs { get; init; } = [];
    }

    private sealed class SelectTestResultsArtifactResult
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("size_in_bytes")]
        public long SizeInBytes { get; init; }
    }
}