// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the pure decision/formatting helpers in
/// .github/workflows/monitor-scheduled-workflows.js.
/// </summary>
public sealed class MonitorScheduledWorkflowsTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public MonitorScheduledWorkflowsTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "monitor-scheduled-workflows.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildMarkerEmbedsWorkflowFile()
    {
        var marker = await InvokeHarnessAsync<string>("buildMarker", new { workflowFile = "generate-api-diffs.yml" });

        Assert.Equal("<!-- automation-broken:generate-api-diffs.yml -->", marker);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SelectEnabledDropsDisabledEntries()
    {
        var enabled = await InvokeHarnessAsync<WatchEntry[]>(
            "selectEnabled",
            new
            {
                config = new
                {
                    watched = new object[]
                    {
                        new { file = "a.yml", name = "A", enabled = true },
                        new { file = "b.yml", name = "B", enabled = false },
                        new { file = "c.yml", name = "C" },
                    }
                }
            });

        Assert.Equal(2, enabled.Length);
        Assert.Contains(enabled, e => e.File == "a.yml");
        Assert.Contains(enabled, e => e.File == "c.yml");
        Assert.DoesNotContain(enabled, e => e.File == "b.yml");
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueTitleIncludesDisplayName()
    {
        var title = await InvokeHarnessAsync<string>("buildIssueTitle", new { displayName = "Generate API Diffs" });

        Assert.Equal("Scheduled workflow failing: Generate API Diffs", title);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DecideActionRecordsWhenFailingWithoutIssue()
    {
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion = "failure", issue = (object?)null });

        Assert.Equal("record", result.Action);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timed_out")]
    [InlineData("startup_failure")]
    [RequiresTools(["node"])]
    public async Task DecideActionRecordsWhenFailingWithExistingIssue(string conclusion)
    {
        // Dedup of an already-recorded run is handled downstream (recordRun scans
        // comments), so a still-failing run always resolves to 'record' here.
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion, issue = new { number = 7 } });

        Assert.Equal("record", result.Action);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DecideActionClosesWhenSucceedingWithExistingIssue()
    {
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion = "success", issue = new { number = 7 } });

        Assert.Equal("close", result.Action);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DecideActionNoopsWhenSucceedingWithoutIssue()
    {
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion = "success", issue = (object?)null });

        Assert.Equal("noop", result.Action);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData(null)]
    [RequiresTools(["node"])]
    public async Task DecideActionNoopsForNonActionableConclusions(string? conclusion)
    {
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion, issue = new { number = 7 } });

        Assert.Equal("noop", result.Action);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyIsStaticDescriptionWithMarker()
    {
        // Each failed run is recorded as a comment, so the body is a fixed
        // description: the marker (for lookup) plus prose, with no per-run table.
        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- automation-broken:locker.yml -->",
                displayName = "Lock Threads",
                workflowFile = "locker.yml",
            });

        Assert.StartsWith("<!-- automation-broken:locker.yml -->", body);
        Assert.Contains("is failing", body);
        Assert.Contains("closed", body);
        Assert.DoesNotContain("automation-broken-failures:begin", body);
        Assert.DoesNotContain("[run #", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentDescribesRunAndConclusion()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                runUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                runNumber = 42,
                sha = "abcdef1234567890",
                conclusion = "failure",
            });

        Assert.Contains("[run #42](https://github.com/microsoft/aspire/actions/runs/123)", comment);
        Assert.Contains("`abcdef12`", comment);
        Assert.Contains("`failure`", comment);
    }

    [Theory]
    [InlineData("startup_failure", "record")]
    [InlineData("timed_out", "record")]
    [InlineData("failure", "noop")]
    [RequiresTools(["node"])]
    public async Task DecideActionForSelfReportsBackstopsStartupAndTimeoutButNotFailure(string conclusion, string expected)
    {
        // selfReports entries own their plain failures in-pipeline, so the watchdog
        // records only startup_failure/timed_out (which the in-pipeline if:failure()
        // reporter cannot catch) and no-ops on a plain failure to avoid double-filing.
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion, issue = new { number = 7 }, selfReports = true });

        Assert.Equal(expected, result.Action);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DecideActionForSelfReportsClosesOnGreen()
    {
        var result = await InvokeHarnessAsync<DecideActionResult>(
            "decideAction",
            new { conclusion = "success", issue = new { number = 7 }, selfReports = true });

        Assert.Equal("close", result.Action);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyStampsAutoCloseTrue()
    {
        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- automation-broken:locker.yml -->",
                displayName = "Lock Threads",
                workflowFile = "locker.yml",
            });

        // Watchdog issues close on green, so they carry the auto-close stamp.
        Assert.Contains("<!-- autoclose:true -->", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyForSelfReportsExplainsBackstopScope()
    {
        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- automation-broken:tests-outerloop.yml -->",
                displayName = "Outerloop Tests",
                workflowFile = "tests-outerloop.yml",
                selfReports = true,
            });

        Assert.Contains("failed to start or timed out", body);
        Assert.Contains("reported separately", body);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "monitor-scheduled-workflows");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);

    private sealed record DecideActionResult(string Action, string Reason);

    private sealed record WatchEntry(string File, string? Name, bool? Enabled);
}
