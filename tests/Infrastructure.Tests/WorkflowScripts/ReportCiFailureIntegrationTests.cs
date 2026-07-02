// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Integration tests for the reportFailure() and resolveSuccess() orchestrators in
/// .github/workflows/report-ci-failure.js, driven against an in-memory octokit fake
/// via report-ci-failure.integration.harness.js. These cover the find-or-create +
/// comment-dedup branching (reportFailure) and the find-and-close branching
/// (resolveSuccess) that the pure-helper tests cannot reach.
/// </summary>
public sealed class ReportCiFailureIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    // The harness reads the exact env-var name (REF); serialize the request verbatim
    // so the Web camelCase policy does not rename it.
    private static readonly JsonSerializerOptions s_requestOptions = new();

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public ReportCiFailureIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "report-ci-failure.integration.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportFailureFilesAutoCloseIssueAndRecordsRunAsComment()
    {
        var result = await InvokeAsync(new
        {
            operation = "reportFailure",
            env = new { REF = "main" },
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("CI failing on `main`", issue.Title);
        Assert.Contains("ci-failure:ci.yml:push:main", issue.Body);
        Assert.Contains("<!-- autoclose:true -->", issue.Body);
        Assert.Equal(["automation-broken"], issue.Labels);

        var comment = Assert.Single(issue.Comments);
        Assert.Contains("/actions/runs/12345", comment);
        Assert.Contains("<!-- run:12345 -->", comment);
        Assert.DoesNotContain("/actions/runs/12345", issue.Body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportFailureKeysSeparateIssuesPerBranch()
    {
        // A red `release/13.3` must not comment on `main`'s issue: the per-branch
        // marker keeps them distinct, so a fresh issue is filed.
        var result = await InvokeAsync(new
        {
            operation = "reportFailure",
            env = new { REF = "release/13.3" },
            issues = new[]
            {
                new { number = 4242, body = "<!-- ci-failure:ci.yml:push:main -->", state = "open" },
            },
        });

        Assert.Contains("create", result.Calls);
        var created = Assert.Single(result.Issues, issue => issue.Number != 4242);
        Assert.Contains("ci-failure:ci.yml:push:release/13.3", created.Body);
        var mainIssue = Assert.Single(result.Issues, issue => issue.Number == 4242);
        Assert.Empty(mainIssue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportFailureCommentsOnExistingIssueForNewRun()
    {
        var first = await InvokeAsync(new { operation = "reportFailure", env = new { REF = "main" } });
        var issue = Assert.Single(first.Issues);

        var second = await InvokeAsync(new
        {
            operation = "reportFailure",
            env = new { REF = "main" },
            issues = new[] { new { number = issue.Number, body = issue.Body, state = "open", comments = issue.Comments } },
            runId = 67890,
            runNumber = 8,
        });

        var updated = Assert.Single(second.Issues);
        Assert.DoesNotContain("create", second.Calls);
        Assert.Equal(2, updated.Comments.Length);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportFailureCommentFailureLeavesRunUnrecorded()
    {
        var result = await InvokeAsync(new
        {
            operation = "reportFailure",
            env = new { REF = "main" },
            failComment = true,
        });

        Assert.True(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Empty(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolveSuccessClosesTheBranchIssue()
    {
        var result = await InvokeAsync(new
        {
            operation = "resolveSuccess",
            env = new { REF = "main" },
            issues = new[]
            {
                new { number = 7, body = "lead <!-- ci-failure:ci.yml:push:main -->\n<!-- autoclose:true -->", state = "open" },
            },
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.Equal("completed", issue.StateReason);
        // A closing comment is posted so the thread records why it closed.
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("green again", comment);
        Assert.Equal(["update", "createComment"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolveSuccessLeavesIssueOpenWhenAutoCloseStampIsMissing()
    {
        var result = await InvokeAsync(new
        {
            operation = "resolveSuccess",
            env = new { REF = "main" },
            issues = new[]
            {
                new { number = 7, body = "lead <!-- ci-failure:ci.yml:push:main -->", state = "open" },
            },
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Empty(issue.Comments);
        Assert.Empty(result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolveSuccessDoesNotCommentWhenCloseFails()
    {
        var result = await InvokeAsync(new
        {
            operation = "resolveSuccess",
            env = new { REF = "main" },
            failUpdate = true,
            issues = new[]
            {
                new { number = 7, body = "lead <!-- ci-failure:ci.yml:push:main -->\n<!-- autoclose:true -->", state = "open" },
            },
        });

        Assert.True(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Empty(issue.Comments);
        Assert.Equal(["update"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolveSuccessIsNoopWhenNoIssueOpen()
    {
        var result = await InvokeAsync(new
        {
            operation = "resolveSuccess",
            env = new { REF = "main" },
        });

        Assert.False(result.Threw);
        Assert.Empty(result.Issues);
        Assert.DoesNotContain("update", result.Calls);
        Assert.DoesNotContain("createComment", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ResolveSuccessLeavesAnotherBranchIssueOpen()
    {
        // A green `main` push must not close `release/13.3`'s open issue.
        var result = await InvokeAsync(new
        {
            operation = "resolveSuccess",
            env = new { REF = "main" },
            issues = new[]
            {
                new { number = 9, body = "<!-- ci-failure:ci.yml:push:release/13.3 -->", state = "open" },
            },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.DoesNotContain("update", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TrackOpensIssueWhenCiRed()
    {
        var result = await InvokeAsync(new
        {
            operation = "track",
            env = new { REF = "main", CI_RED = "true", CI_GREEN = "false" },
        });

        Assert.False(result.Threw);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("CI failing on `main`", issue.Title);
        Assert.Contains("create", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TrackClosesIssueWhenCiGreen()
    {
        var result = await InvokeAsync(new
        {
            operation = "track",
            env = new { REF = "main", CI_RED = "false", CI_GREEN = "true" },
            issues = new[]
            {
                new { number = 7, body = "lead <!-- ci-failure:ci.yml:push:main -->\n<!-- autoclose:true -->", state = "open" },
            },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TrackIsNoopWhenNeitherRedNorGreen()
    {
        // A skipped (no-relevant-changes) push: CI_RED and CI_GREEN both false, so a
        // still-open red-main issue must be left untouched (no CI signal).
        var result = await InvokeAsync(new
        {
            operation = "track",
            env = new { REF = "main", CI_RED = "false", CI_GREEN = "false" },
            issues = new[]
            {
                new { number = 7, body = "lead <!-- ci-failure:ci.yml:push:main -->", state = "open" },
            },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.DoesNotContain("update", result.Calls);
        Assert.DoesNotContain("createComment", result.Calls);
    }

    private async Task<RunnerResult> InvokeAsync(object scenario)
    {
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(scenario, s_requestOptions));

        using var command = new NodeCommand(_output, "report-ci-failure-integration");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse(RunnerResult Result);

    private sealed record RunnerResult(bool Threw, string[] Calls, RunnerIssue[] Issues);

    private sealed record RunnerIssue(int Number, string? Title, string State, string? StateReason, string Body, string[] Labels, string[] Comments);
}
