// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Integration tests for the network orchestration in
/// .github/workflows/specialized-test-failure-runner.js, driven against an
/// in-memory octokit fake via specialized-test-failure-runner.harness.js. These
/// cover the results-read/classify preamble plus the find-or-create + comment-dedup
/// branching (delegated to the shared engine) that the pure-helper tests cannot reach.
/// </summary>
public sealed class SpecializedTestFailureRunnerTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    // The harness reads exact env-var names (WORKFLOW_FILE, ...) and helper keys
    // (failedTests, ...); serialize the request verbatim so the Web camelCase policy
    // does not rename them.
    private static readonly JsonSerializerOptions s_requestOptions = new();

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public SpecializedTestFailureRunnerTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "specialized-test-failure-runner.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    private static object OuterloopEnv() => new
    {
        WORKFLOW_FILE = "tests-outerloop.yml",
        DISPLAY_NAME = "Outerloop Tests",
        IGNORE_TEST_FAILURES = "false",
    };

    [Fact]
    [RequiresTools(["node"])]
    public async Task FilesTestFailuresIssueAndRecordsRunAsComment()
    {
        var result = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A", "Tests.Type.B" },
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Contains("ci-failure:tests-outerloop.yml:test-failures", issue.Body);

        // The failing-test list and run link ride on the comment, with the hidden
        // dedup marker; the body is a static description.
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("`Tests.Type.A`", comment);
        Assert.Contains("/actions/runs/12345", comment);
        Assert.Contains("<!-- run:12345 -->", comment);
        Assert.DoesNotContain("/actions/runs/12345", issue.Body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CommentFailureLeavesRunUnrecorded()
    {
        // If the comment (which carries the failing-test list) fails, no comment is
        // recorded, so the dedup guard cannot suppress the list on the next run.
        var result = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A" },
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
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A" },
            failComment = true,
        });
        var stranded = Assert.Single(first.Issues);
        Assert.Empty(stranded.Comments);

        // Re-run (same runId, comment succeeds): the list is posted once and recorded
        // as a comment.
        var second = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A" },
            issues = new[] { new { number = stranded.Number, body = stranded.Body, state = "open", comments = stranded.Comments } },
        });
        var recorded = Assert.Single(second.Issues);
        Assert.Single(recorded.Comments);
        Assert.Contains("<!-- run:12345 -->", recorded.Comments[0]);

        // A further tick for the same run must not re-notify.
        var third = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A" },
            issues = new[] { new { number = recorded.Number, body = recorded.Body, state = "open", comments = recorded.Comments } },
        });
        Assert.DoesNotContain("createComment", third.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MissingResultsPathIsClassifiedAsTestFailures()
    {
        // The extract step crashed before emitting its path (FAILED_TESTS_PATH
        // empty). A red outerloop run must be filed as a test failure, not infra.
        var result = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            omitFailedTestsPath = true,
        });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("ci-failure:tests-outerloop.yml:test-failures", issue.Body);
        Assert.Contains("could not be extracted", Assert.Single(issue.Comments));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task QuarantineFailureFilesInfraIssue()
    {
        var result = await InvokeAsync(new
        {
            env = new
            {
                WORKFLOW_FILE = "tests-quarantine.yml",
                DISPLAY_NAME = "Quarantined Tests",
                IGNORE_TEST_FAILURES = "true",
            },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("ci-failure:tests-quarantine.yml:infra", issue.Body);
        Assert.Contains("Infrastructure failure", Assert.Single(issue.Comments));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PullRequestCarryingMarkerIsIgnored()
    {
        // listForRepo returns PRs too. A PR labelled automation-broken/failing-test
        // whose body happens to contain the marker must not be mistaken for the
        // managed issue: the runner files a fresh issue instead of commenting on the PR.
        var result = await InvokeAsync(new
        {
            env = OuterloopEnv(),
            failedTests = new[] { "Tests.Type.A" },
            issues = new[]
            {
                new
                {
                    number = 4242,
                    body = "<!-- ci-failure:tests-outerloop.yml:test-failures -->",
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
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(scenario, s_requestOptions));

        using var command = new NodeCommand(_output, "specialized-test-failure-runner");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse(RunnerResult Result);

    private sealed record RunnerResult(bool Threw, string[] Calls, RunnerIssue[] Issues);

    private sealed record RunnerIssue(int Number, string? Title, string State, string Body, string[] Comments);
}
