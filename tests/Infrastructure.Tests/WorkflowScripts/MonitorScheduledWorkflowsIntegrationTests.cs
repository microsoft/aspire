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

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public MonitorScheduledWorkflowsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "monitor-scheduled-workflows.integration.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    // `id` is the stable run identifier the runner uses as the comment dedup key.
    private static object FailingRun() => new
    {
        id = 9,
        conclusion = "failure",
        html_url = "https://github.com/microsoft/aspire/actions/runs/9",
        run_number = 9,
        head_sha = "abcdef1234567890",
        updated_at = "2026-01-01T00:00:00Z",
    };

    private static object SucceedingRun() => new
    {
        id = 10,
        conclusion = "success",
        html_url = "https://github.com/microsoft/aspire/actions/runs/10",
        run_number = 10,
        head_sha = "0123456789abcdef",
        updated_at = "2026-01-02T00:00:00Z",
    };

    private static object StartupFailureRun() => new
    {
        id = 11,
        conclusion = "startup_failure",
        html_url = "https://github.com/microsoft/aspire/actions/runs/11",
        run_number = 11,
        head_sha = "fedcba9876543210",
        updated_at = "2026-01-03T00:00:00Z",
    };

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
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
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

    private sealed record MonitorResult(bool Threw, string[] Calls, MonitorIssue[] Issues);

    private sealed record MonitorIssue(int Number, string State, string Body, string[] Labels, string[] Comments);
}
