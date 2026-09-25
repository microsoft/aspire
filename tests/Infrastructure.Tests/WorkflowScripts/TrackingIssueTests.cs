// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the generic, repo-agnostic tracking-issue engine in
/// .github/workflows/tracking-issue.js: marker dedup and the comment-based
/// recordRun loop (find-or-create the issue, then record each run as a comment,
/// deduping on the hidden per-run marker).
/// </summary>
public sealed class TrackingIssueTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public TrackingIssueTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "tracking-issue.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task FindOpenIssueForMarkerReturnsOldestMatch()
    {
        var marker = "<!-- x -->";
        var result = await InvokeHarnessAsync<FindIssueResult>(
            "findOpenIssueForMarker",
            new
            {
                marker,
                issues = new object[]
                {
                    new { number = 40, body = $"a {marker}" },
                    new { number = 11, body = marker },
                    new { number = 88, body = "other" },
                }
            });

        Assert.Equal(11, result.Number);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FindOpenIssueForMarkerReturnsNullWhenNoMatch()
    {
        var result = await InvokeHarnessAsync<FindIssueResult>(
            "findOpenIssueForMarker",
            new { marker = "<!-- x -->", issues = new object[] { new { number = 1, body = "nope" } } });

        Assert.Null(result.Number);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RunMarkerEmbedsRunIdAsHtmlComment()
    {
        var marker = await InvokeHarnessAsync<string>("runMarker", new { runId = 1234 });

        Assert.Equal("<!-- run:1234 -->", marker);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunFilesIssueAndCommentsWhenNoneExists()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new { marker = "<!-- m -->", runId = 9, comment = "boom" });

        Assert.True(result.Result.Created);
        Assert.False(result.Result.Skipped);
        Assert.Contains("create", result.Calls);
        Assert.Contains("createComment", result.Calls);

        var issue = Assert.Single(result.Issues);
        var comment = Assert.Single(issue.Comments);
        Assert.Contains("boom", comment);
        Assert.Contains("<!-- run:9 -->", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunCommentsOnExistingIssueForNewRun()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                comment = "again",
                issues = new object[]
                {
                    new { number = 5, body = "lead <!-- m -->", comments = new[] { "first <!-- run:6 -->" } },
                }
            });

        Assert.False(result.Result.Created);
        Assert.False(result.Result.Skipped);
        Assert.DoesNotContain("create", result.Calls);
        Assert.Contains("createComment", result.Calls);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(2, issue.Comments.Length);
        Assert.Contains(issue.Comments, c => c.Contains("<!-- run:7 -->"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunSkipsWhenRunAlreadyRecorded()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 6,
                comment = "dup",
                issues = new object[]
                {
                    new { number = 5, body = "lead <!-- m -->", comments = new[] { "first <!-- run:6 -->" } },
                }
            });

        Assert.True(result.Result.Skipped);
        Assert.False(result.Result.Created);
        Assert.DoesNotContain("createComment", result.Calls);

        var issue = Assert.Single(result.Issues);
        Assert.Single(issue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunReopensClosedIssueForNewRun()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                comment = "again",
                issues = new object[]
                {
                    new { number = 5, body = "lead <!-- m -->", state = "closed", comments = new[] { "first <!-- run:6 -->" } },
                }
            });

        Assert.False(result.Result.Created);
        Assert.False(result.Result.Skipped);
        Assert.Equal(["update", "createComment"], result.Calls);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Equal(2, issue.Comments.Length);
        Assert.Contains(issue.Comments, c => c.Contains("<!-- run:7 -->"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunPrefersOpenIssueOverOlderClosedDuplicate()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                comment = "again",
                issues = new object[]
                {
                    new { number = 5, body = "lead <!-- m -->", state = "closed", comments = new[] { "first <!-- run:6 -->" } },
                    new { number = 8, body = "lead <!-- m -->", state = "open", comments = Array.Empty<string>() },
                }
            });

        Assert.False(result.Result.Created);
        Assert.False(result.Result.Skipped);
        Assert.Equal(["createComment"], result.Calls);

        var closedIssue = Assert.Single(result.Issues, issue => issue.Number == 5);
        Assert.Equal("closed", closedIssue.State);
        Assert.Single(closedIssue.Comments);

        var openIssue = Assert.Single(result.Issues, issue => issue.Number == 8);
        Assert.Equal("open", openIssue.State);
        var comment = Assert.Single(openIssue.Comments);
        Assert.Contains("<!-- run:7 -->", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildBodyEmbedsAutoCloseStampWhenTrue()
    {
        var body = await InvokeHarnessAsync<string>(
            "buildBody",
            new { marker = "<!-- m -->", autoClose = true });

        Assert.Contains("<!-- m -->", body);
        Assert.Contains("<!-- autoclose:true -->", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildBodyEmbedsAutoCloseStampWhenFalse()
    {
        var body = await InvokeHarnessAsync<string>(
            "buildBody",
            new { marker = "<!-- m -->", autoClose = false });

        Assert.Contains("<!-- autoclose:false -->", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildBodyOmitsAutoCloseStampWhenUnset()
    {
        var body = await InvokeHarnessAsync<string>(
            "buildBody",
            new { marker = "<!-- m -->" });

        Assert.DoesNotContain("autoclose", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReadAutoCloseReturnsTrueForTrueStamp()
    {
        var result = await InvokeHarnessAsync<ReadAutoCloseResult>(
            "readAutoClose",
            new { body = "lead\n<!-- autoclose:true -->\nmore" });

        Assert.True(result.Value);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReadAutoCloseReturnsFalseForFalseStamp()
    {
        var result = await InvokeHarnessAsync<ReadAutoCloseResult>(
            "readAutoClose",
            new { body = "<!--autoclose:false-->" });

        Assert.False(result.Value);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReadAutoCloseReturnsNullWhenStampMissing()
    {
        var result = await InvokeHarnessAsync<ReadAutoCloseResult>(
            "readAutoClose",
            new { body = "a body with no stamp" });

        Assert.Null(result.Value);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReadAutoCloseReturnsNullWhenStampUnparseable()
    {
        var result = await InvokeHarnessAsync<ReadAutoCloseResult>(
            "readAutoClose",
            new { body = "<!-- autoclose:maybe -->" });

        Assert.Null(result.Value);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "tracking-issue");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);

    private sealed record FindIssueResult(int? Number);

    private sealed record ReadAutoCloseResult(bool? Value);

    private sealed record RecordRunResult(RecordResult Result, string[] Calls, IssueState[] Issues);

    private sealed record RecordResult(int Number, bool Created, bool Skipped);

    private sealed record IssueState(int Number, string State, string Body, string[] Labels, string[] Comments);
}