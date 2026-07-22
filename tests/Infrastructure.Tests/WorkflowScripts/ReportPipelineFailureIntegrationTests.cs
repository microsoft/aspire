// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Integration tests for the report() orchestrator in
/// .github/workflows/report-pipeline-failure.js, driven against an in-memory
/// octokit fake via report-pipeline-failure.integration.harness.js. These cover the
/// find-or-create + comment-dedup branching (delegated to the shared engine) that
/// the pure-helper tests cannot reach.
/// </summary>
public sealed class ReportPipelineFailureIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    // The harness reads exact env-var names (WORKFLOW_FILE, ...); serialize the
    // request verbatim so the Web camelCase policy does not rename them.
    private static readonly JsonSerializerOptions s_requestOptions = new();

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public ReportPipelineFailureIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "report-pipeline-failure.integration.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    private static object DeploymentEnv() => new
    {
        WORKFLOW_FILE = "deployment-tests.yml",
        DISPLAY_NAME = "Deployment E2E Tests",
    };

    private static readonly string[] s_deploymentLabels = ["automation-broken", "area-testing", "deployment-e2e"];

    [Fact]
    [RequiresTools(["node"])]
    public async Task FilesIssueWithAllLabelsAndRecordsRunAsComment()
    {
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            cc = "@microsoft/aspire-team",
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Nightly run failing: Deployment E2E Tests", issue.Title);
        Assert.Contains("ci-failure:deployment-tests.yml:scheduled", issue.Body);
        // Carries the existing labels PLUS automation-broken.
        Assert.Equal(s_deploymentLabels, issue.Labels);
        Assert.Contains("/cc @microsoft/aspire-team", issue.Body);

        // The run is recorded as a comment, not in the body; the comment carries the
        // run link and the hidden dedup marker.
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("/actions/runs/12345", comment);
        Assert.Contains("<!-- run:12345 -->", comment);
        Assert.DoesNotContain("/actions/runs/12345", issue.Body);
        Assert.Contains("create", result.Calls);
        Assert.Contains("createComment", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommentDetailRidesOnTheComment()
    {
        var result = await InvokeAsync(new
        {
            env = new { WORKFLOW_FILE = "tests-daily-smoke.yml", DISPLAY_NAME = "Daily CLI Smoke Tests" },
            labels = new[] { "automation-broken", "area-cli", "failing-test" },
            commentDetail = "### Aspire CLI versions tested\n\n- 9.0.0",
        });

        var issue = Assert.Single(result.Issues);
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("### Aspire CLI versions tested", comment);
        // Per-run detail goes on the comment, not baked into the issue body.
        Assert.DoesNotContain("### Aspire CLI versions tested", issue.Body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ForcesAutomationBrokenLabelEvenIfCallerOmitsIt()
    {
        // The runner looks issues up by automation-broken, so it must also file with
        // it. A caller that passes only its own labels must still get automation-broken
        // added, or the next run would not find the issue and would file a duplicate.
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = new[] { "area-testing", "deployment-e2e" },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("automation-broken", issue.Labels);
        Assert.Contains("deployment-e2e", issue.Labels);
        // Added once, not duplicated.
        Assert.Single(issue.Labels, label => label == "automation-broken");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommentFailureLeavesRunUnrecorded()
    {
        // If the comment fails, no comment is recorded, so the dedup guard cannot
        // suppress the notification on the next run for this run.
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            failComment = true,
        });

        Assert.True(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Empty(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RetryAfterCommentFailurePostsExactlyOnceThenDedups()
    {
        var first = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            failComment = true,
        });
        var stranded = Assert.Single(first.Issues);
        Assert.Empty(stranded.Comments);

        // Re-run (same runId, comment succeeds): notified once and the run is recorded
        // as a comment.
        var second = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[] { new { number = stranded.Number, body = stranded.Body, state = "open", comments = stranded.Comments } },
        });
        var recorded = Assert.Single(second.Issues);
        Assert.Single(recorded.Comments);
        Assert.Contains("<!-- run:12345 -->", recorded.Comments[0]);

        // A further tick for the same run must not re-notify (the comment already
        // carries the run marker).
        var third = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[] { new { number = recorded.Number, body = recorded.Body, state = "open", comments = recorded.Comments } },
        });
        Assert.DoesNotContain("createComment", third.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommentsOnExistingIssueForNewRun()
    {
        var first = await InvokeAsync(new { env = DeploymentEnv(), labels = s_deploymentLabels });
        var issue = Assert.Single(first.Issues);

        // A later scheduled run (new runId) adds a second comment to the same issue
        // rather than filing a new one.
        var second = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[] { new { number = issue.Number, body = issue.Body, state = "open", comments = issue.Comments } },
            runId = 67890,
            runNumber = 8,
        });

        var updated = Assert.Single(second.Issues);
        Assert.DoesNotContain("create", second.Calls);
        Assert.Equal(2, updated.Comments.Length);
        Assert.Contains(updated.Comments, c => c.Contains("/actions/runs/12345"));
        Assert.Contains(updated.Comments, c => c.Contains("/actions/runs/67890"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReopensClosedIssueForNewRun()
    {
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[]
            {
                new
                {
                    number = 4242,
                    body = "<!-- ci-failure:deployment-tests.yml:scheduled -->\n\nExisting closed issue.",
                    state = "closed",
                    comments = Array.Empty<string>(),
                },
            },
            runId = 67890,
            runNumber = 8,
        });

        Assert.DoesNotContain("create", result.Calls);
        Assert.Contains("update", result.Calls);
        Assert.Contains("createComment", result.Calls);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(4242, issue.Number);
        Assert.Equal("open", issue.State);

        var comment = Assert.Single(issue.Comments);
        Assert.Contains("/actions/runs/67890", comment);
        Assert.Contains("<!-- run:67890 -->", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotManageAnotherWorkflowsAutomationBrokenIssue()
    {
        // Both pipelines and the scanner/specialized reporter carry automation-broken,
        // so the label query is a superset. The per-workflow marker must keep this
        // reporter from commenting on a different workflow's issue: it files its own.
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[]
            {
                new
                {
                    number = 4242,
                    body = "<!-- ci-failure:tests-outerloop.yml:infra -->",
                    state = "open",
                },
            },
        });

        Assert.Contains("create", result.Calls);
        var created = Assert.Single(result.Issues, issue => issue.Number != 4242);
        Assert.Contains("ci-failure:deployment-tests.yml:scheduled", created.Body);
        var other = Assert.Single(result.Issues, issue => issue.Number == 4242);
        Assert.Empty(other.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PullRequestCarryingMarkerIsIgnored()
    {
        // listForRepo returns PRs too. A PR labelled automation-broken whose body
        // happens to contain the marker must not be mistaken for the managed issue:
        // the runner files a fresh issue instead of commenting on the PR.
        var result = await InvokeAsync(new
        {
            env = DeploymentEnv(),
            labels = s_deploymentLabels,
            issues = new[]
            {
                new
                {
                    number = 4242,
                    body = "<!-- ci-failure:deployment-tests.yml:scheduled -->",
                    state = "open",
                    pull_request = new { url = "https://api.github.com/pr/4242" },
                },
            },
        });

        Assert.Contains("create", result.Calls);
        var created = Assert.Single(result.Issues, issue => issue.Number != 4242);
        Assert.Contains(created.Comments, c => c.Contains("/actions/runs/12345"));
        var pr = Assert.Single(result.Issues, issue => issue.Number == 4242);
        Assert.Empty(pr.Comments);
    }

    private async Task<RunnerResult> InvokeAsync(object scenario)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(scenario, s_requestOptions));

        using var command = new NodeCommand(_output, "report-pipeline-failure-integration");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse(RunnerResult Result);

    private sealed record RunnerResult(bool Threw, string[] Calls, RunnerIssue[] Issues);

    private sealed record RunnerIssue(int Number, string? Title, string State, string Body, string[] Labels, string[] Comments);
}