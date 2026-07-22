// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Integration tests for the run() orchestrator in
/// .github/workflows/monitor-scheduled-workflows.js, driven against an
/// in-memory octokit fake via monitor-scheduled-workflows.integration.harness.js. These
/// cover the dry-run no-mutation contract, comment-based dedup, and close-on-green,
/// which the pure-helper tests cannot reach.
/// </summary>
public sealed class MonitorScheduledWorkflowsIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    // Serialize the request verbatim so the Web camelCase policy does not rename the
    // run fields (html_url, run_number, ...) the runner reads.
    private static readonly JsonSerializerOptions s_requestOptions = new();

    // A watched workflow (see .github/workflows/monitor-scheduled-workflows.config.json).
    private const string WatchedFile = "generate-api-diffs.yml";
    private const string Marker = "<!-- automation-broken:generate-api-diffs.yml -->";

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public MonitorScheduledWorkflowsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "monitor-scheduled-workflows.integration.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    // `id` is the stable run identifier the runner uses as the comment dedup key.
    private static object FailingRun(int id = 9, int runNumber = 9, string? updatedAt = null) => new
    {
        id,
        conclusion = "failure",
        html_url = $"https://github.com/microsoft/aspire/actions/runs/{id}",
        run_number = runNumber,
        head_sha = "abcdef1234567890",
        updated_at = updatedAt ?? MinutesAgo(30),
    };

    private static object SucceedingRun(int id = 10, int runNumber = 10, string? updatedAt = null) => new
    {
        id,
        conclusion = "success",
        html_url = $"https://github.com/microsoft/aspire/actions/runs/{id}",
        run_number = runNumber,
        head_sha = "0123456789abcdef",
        updated_at = updatedAt ?? MinutesAgo(30),
    };

    private static object StartupFailureRun(int id = 11, int runNumber = 11, string? updatedAt = null) => new
    {
        id,
        conclusion = "startup_failure",
        html_url = $"https://github.com/microsoft/aspire/actions/runs/{id}",
        run_number = runNumber,
        head_sha = "fedcba9876543210",
        updated_at = updatedAt ?? MinutesAgo(30),
    };

    private static string MinutesAgo(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToString("O");

    [Fact]
    [RequiresTools(["node"])]
    public async Task DryRunDoesNotMutateGitHub()
    {
        // The workflow_dispatch dry_run input promises no GitHub mutation. Even with
        // a workflow failing (which would otherwise file an issue), nothing — not
        // even the label — may be created.
        var result = await InvokeAsync(new
        {
            dryRun = true,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = FailingRun() },
        });

        Assert.DoesNotContain("createLabel", result.Calls);
        Assert.DoesNotContain("create", result.Calls);
        Assert.DoesNotContain("update", result.Calls);
        Assert.DoesNotContain("createComment", result.Calls);
        Assert.Empty(result.Issues);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RealRunFilesIssueAndRecordsFailureComment()
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = FailingRun() },
        });

        Assert.Contains("createLabel", result.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Contains(Marker, issue.Body);
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("<!-- run:9 -->", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DedupsWhenLatestFailedRunAlreadyRecorded()
    {
        // The scanner runs every 2h but watched workflows run less often, so the same
        // still-latest failed run is seen repeatedly. A run whose comment already
        // exists must not be re-commented on each tick.
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = FailingRun() },
            issues = new[]
            {
                new { number = 55, body = Marker, state = "open", comments = new[] { "earlier <!-- run:9 -->" } },
            },
        });

        Assert.DoesNotContain("createComment", result.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Single(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DryRunDoesNotLogRecordWhenFailedRunAlreadyRecorded()
    {
        var result = await InvokeAsync(new
        {
            dryRun = true,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = FailingRun(updatedAt: MinutesAgo(30)) },
            issues = new[]
            {
                new { number = 55, body = Marker, state = "open", comments = new[] { "earlier <!-- run:9 -->" } },
            },
        });

        Assert.DoesNotContain(result.Logs, log => log.Contains("would RECORD", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Calls, call => call is "create" or "update" or "createComment");
        var issue = Assert.Single(result.Issues);
        Assert.Single(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClosesIssueWhenLatestRunIsGreen()
    {
        // A successful latest run with an open issue closes it automatically, with a
        // closing comment.
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = SucceedingRun() },
            issues = new[]
            {
                new { number = 55, body = $"{Marker}\n<!-- autoclose:true -->", state = "open", comments = Array.Empty<string>() },
            },
        });

        Assert.False(result.Threw);
        Assert.Equal(["createLabel", "update", "createComment"], result.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LeavesIssueOpenWhenAutoCloseStampIsFalse()
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = SucceedingRun() },
            issues = new[]
            {
                new { number = 55, body = $"{Marker}\n<!-- autoclose:false -->", state = "open", comments = Array.Empty<string>() },
            },
        });

        Assert.False(result.Threw);
        Assert.Equal(["createLabel"], result.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Empty(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DryRunDoesNotLogCloseWhenAutoCloseStampIsFalse()
    {
        var result = await InvokeAsync(new
        {
            dryRun = true,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = SucceedingRun(updatedAt: MinutesAgo(30)) },
            issues = new[]
            {
                new { number = 55, body = $"{Marker}\n<!-- autoclose:false -->", state = "open", comments = Array.Empty<string>() },
            },
        });

        Assert.DoesNotContain(result.Logs, log => log.Contains("would CLOSE", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Calls, call => call is "update" or "createComment");
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Empty(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DryRunLogsCloseAfterWouldRecordWhenNewestRunSucceeded()
    {
        var result = await InvokeAsync(new
        {
            dryRun = true,
            runsByFile = new Dictionary<string, object>
            {
                [WatchedFile] = new[]
                {
                    FailingRun(id: 9, runNumber: 9, updatedAt: MinutesAgo(75)),
                    SucceedingRun(id: 10, runNumber: 10, updatedAt: MinutesAgo(15)),
                },
            },
        });

        Assert.Contains(result.Logs, log => log.Contains("would RECORD failure", StringComparison.Ordinal));
        Assert.Contains(result.Logs, log => log.Contains("would CLOSE", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Calls, call => call is "create" or "update" or "createComment");
        Assert.Empty(result.Issues);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DryRunRecordsMultipleFailuresInWindowWithoutReadingPlaceholderComments()
    {
        var result = await InvokeAsync(new
        {
            dryRun = true,
            runsByFile = new Dictionary<string, object>
            {
                [WatchedFile] = new[]
                {
                    FailingRun(id: 9, runNumber: 9, updatedAt: MinutesAgo(75)),
                    FailingRun(id: 10, runNumber: 10, updatedAt: MinutesAgo(15)),
                },
            },
        });

        Assert.False(result.Threw);
        Assert.Equal(2, result.Logs.Count(log => log.Contains("would RECORD failure", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Calls, call => call is "create" or "update" or "createComment");
        Assert.Empty(result.Issues);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotCommentWhenCloseFails()
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            failUpdate = true,
            runsByFile = new Dictionary<string, object> { [WatchedFile] = SucceedingRun() },
            issues = new[]
            {
                new { number = 55, body = $"{Marker}\n<!-- autoclose:true -->", state = "open", comments = Array.Empty<string>() },
            },
        });

        Assert.True(result.Threw);
        Assert.Equal(["createLabel", "update"], result.Calls);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Empty(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ProcessesRecentFailureEvenWhenNewerRunSucceeded()
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object>
            {
                [WatchedFile] = new[]
                {
                    SucceedingRun(id: 10, runNumber: 10, updatedAt: MinutesAgo(15)),
                    FailingRun(id: 9, runNumber: 9, updatedAt: MinutesAgo(75)),
                },
            },
        });

        Assert.All(result.ListRunRequests, request => Assert.True(request.PerPage > 1, "The watchdog must fetch more than one run per workflow."));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.Contains(issue.Comments, comment => comment.Contains("<!-- run:9 -->", StringComparison.Ordinal));
        Assert.Contains(issue.Comments, comment => comment.Contains("Latest run succeeded", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DoesNotCloseWhenNewestRunInPollingWindowIsRecordedFailure()
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object>
            {
                [WatchedFile] = new[]
                {
                    SucceedingRun(id: 9, runNumber: 9, updatedAt: MinutesAgo(75)),
                    FailingRun(id: 10, runNumber: 10, updatedAt: MinutesAgo(15)),
                },
            },
            issues = new[]
            {
                new { number = 55, body = $"{Marker}\n<!-- autoclose:true -->", state = "open", comments = new[] { "earlier <!-- run:10 -->" } },
            },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.DoesNotContain(issue.Comments, comment => comment.Contains("Latest run succeeded", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelfReportsEntryFilesBackstopIssueWithPerEntryLabelsOnStartupFailure()
    {
        // deployment-tests.yml is a selfReports entry with per-entry labels. A
        // startup_failure (the in-pipeline reporter never ran) files the backstop
        // issue, carrying automation-broken PLUS the configured labels.
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { ["deployment-tests.yml"] = StartupFailureRun() },
        });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("<!-- automation-broken:deployment-tests.yml -->", issue.Body);
        Assert.Contains("automation-broken", issue.Labels);
        Assert.Contains("area-testing", issue.Labels);
        Assert.Contains("deployment-e2e", issue.Labels);
    }

    [Theory]
    [InlineData("tests-outerloop.yml")]
    [InlineData("tests-daily-smoke.yml")]
    [RequiresTools(["node"])]
    public async Task SelfReportsBackstopIssueDoesNotUseFailingTestLabel(string workflowFile)
    {
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { [workflowFile] = StartupFailureRun(updatedAt: MinutesAgo(30)) },
        });

        var issue = Assert.Single(result.Issues);
        Assert.DoesNotContain("failing-test", issue.Labels);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelfReportsEntryDoesNotFileOnPlainFailure()
    {
        // tests-outerloop.yml is a selfReports entry. A plain failure is owned by its
        // in-pipeline reporter, so the watchdog must NOT file a (duplicate) issue.
        var result = await InvokeAsync(new
        {
            dryRun = false,
            runsByFile = new Dictionary<string, object> { ["tests-outerloop.yml"] = FailingRun() },
        });

        Assert.DoesNotContain("create", result.Calls);
        Assert.Empty(result.Issues);
    }

    private async Task<MonitorResult> InvokeAsync(object scenario)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(scenario, s_requestOptions));

        using var command = new NodeCommand(_output, "monitor-scheduled-workflows-integration");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse(MonitorResult Result);

    private sealed record MonitorResult(bool Threw, string[] Calls, MonitorIssue[] Issues, string[] Logs, ListRunRequest[] ListRunRequests);

    private sealed record MonitorIssue(int Number, string State, string Body, string[] Labels, string[] Comments);

    private sealed record ListRunRequest(string WorkflowId, int? PerPage);
}