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
    public async Task RecordRunDoesNotReopenClosedCanonicalWhenRunWasAlreadyRecordedThere()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 6,
                closeDuplicates = true,
                issues = new object[]
                {
                    new { number = 5, body = "<!-- m -->", state = "closed", comments = new[] { "first <!-- run:6 -->" } },
                }
            });

        Assert.True(result.Result.Skipped);
        Assert.Equal("closed", Assert.Single(result.Issues).State);
        Assert.Empty(result.Calls);
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
    public async Task RecordRunKeepsOldestIssueCanonicalAndClosesNewerDuplicates()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                comment = "again",
                closeDuplicates = true,
                issues = new object[]
                {
                    new { number = 5, body = "lead <!-- m -->", state = "closed", comments = new[] { "first <!-- run:6 -->" } },
                    new { number = 8, body = "lead <!-- m -->", state = "open", comments = Array.Empty<string>() },
                }
            });

        Assert.False(result.Result.Created);
        Assert.False(result.Result.Skipped);
        Assert.Equal([8], result.Result.DuplicatesClosed);

        var canonicalIssue = Assert.Single(result.Issues, issue => issue.Number == 5);
        Assert.Equal("open", canonicalIssue.State);
        Assert.Contains(canonicalIssue.Comments, comment => comment.Contains("<!-- run:7 -->", StringComparison.Ordinal));
        Assert.Contains(canonicalIssue.Comments, comment => comment.Contains("#8", StringComparison.Ordinal));

        var duplicateIssue = Assert.Single(result.Issues, issue => issue.Number == 8);
        Assert.Equal("closed", duplicateIssue.State);
        Assert.Equal("not_planned", duplicateIssue.StateReason);
        Assert.Contains(duplicateIssue.Comments, comment => comment.Contains("Duplicate of #5", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunPreservesOpenFirstCompatibilityWithoutDuplicateClosure()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                issues = new object[]
                {
                    new { number = 5, body = "<!-- m -->", state = "closed" },
                    new { number = 8, body = "<!-- m -->", state = "open" },
                }
            });

        Assert.Equal(8, result.Result.Number);
        Assert.Equal("closed", Assert.Single(result.Issues, issue => issue.Number == 5).State);
        Assert.Equal("open", Assert.Single(result.Issues, issue => issue.Number == 8).State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunUsesIdentityPredicateBeforeSelectingCanonicalIssue()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                requiredSecondLine = "<!-- type:infra -->",
                runId = 7,
                closeDuplicates = true,
                issues = new object[]
                {
                    new { number = 5, body = "<!-- m -->\n<!-- type:test -->" },
                    new { number = 8, body = "<!-- m -->\n<!-- type:infra -->" },
                }
            });

        Assert.Equal(8, result.Result.Number);
        Assert.Empty(result.Result.DuplicatesClosed);
        Assert.Equal("open", Assert.Single(result.Issues, issue => issue.Number == 5).State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunRelistsAfterCreationAndClosesConcurrentDuplicate()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                closeDuplicates = true,
                concurrentIssue = new { number = 4, body = "<!-- m -->" },
            });

        Assert.Equal(4, result.Result.Number);
        Assert.False(result.Result.Created);
        Assert.Equal([1000], result.Result.DuplicatesClosed);

        var createdDuplicate = Assert.Single(result.Issues, issue => issue.Number == 1000);
        Assert.Equal("closed", createdDuplicate.State);
        Assert.Equal("not_planned", createdDuplicate.StateReason);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunRelistsAfterCreationWithoutDuplicateClosure()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                concurrentIssue = new { number = 4, body = "<!-- m -->" },
            });

        Assert.Equal(4, result.Result.Number);
        Assert.False(result.Result.Created);
        Assert.Empty(result.Result.DuplicatesClosed);
        Assert.Contains(
            Assert.Single(result.Issues, issue => issue.Number == 4).Comments,
            comment => comment.Contains("<!-- run:7 -->", StringComparison.Ordinal));
        Assert.Equal("open", Assert.Single(result.Issues, issue => issue.Number == 1000).State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunResumesDuplicateClosureWithoutRepeatingReconciliationComments()
    {
        const string reconciliationMarker = "<!-- tracking-issue-duplicate:v1:5:8 -->";
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                closeDuplicates = true,
                issues = new object[]
                {
                    new
                    {
                        number = 5,
                        body = "<!-- m -->",
                        comments = new[] { $"[automated] Issue #8 is a duplicate.\n\n{reconciliationMarker}" },
                    },
                    new
                    {
                        number = 8,
                        body = "<!-- m -->",
                        comments = new[] { $"[automated] Duplicate of #5.\n\n{reconciliationMarker}" },
                    },
                }
            });

        var canonicalIssue = Assert.Single(result.Issues, issue => issue.Number == 5);
        Assert.Equal(2, canonicalIssue.Comments.Length);

        var duplicateIssue = Assert.Single(result.Issues, issue => issue.Number == 8);
        Assert.Equal("closed", duplicateIssue.State);
        Assert.Single(duplicateIssue.Comments);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RecordRunReopensCanonicalWhenOccurrenceWasRecordedOnDuplicate()
    {
        var result = await InvokeHarnessAsync<RecordRunResult>(
            "recordRun",
            new
            {
                marker = "<!-- m -->",
                runId = 7,
                closeDuplicates = true,
                issues = new object[]
                {
                    new { number = 5, body = "<!-- m -->", state = "closed" },
                    new { number = 8, body = "<!-- m -->", comments = new[] { "failure <!-- run:7 -->" } },
                }
            });

        Assert.True(result.Result.Skipped);
        Assert.Equal("open", Assert.Single(result.Issues, issue => issue.Number == 5).State);
        Assert.Equal("closed", Assert.Single(result.Issues, issue => issue.Number == 8).State);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PlannerAndExecutorProduceTheSameDeterministicActions()
    {
        var result = await InvokeHarnessAsync<PlanExecutionResult>(
            "planAndExecute",
            new
            {
                marker = "<!-- m -->",
                updateBody = "<!-- m -->\nupdated",
                comment = "new occurrence",
                closeDuplicates = true,
                issues = new object[]
                {
                    new { number = 8, body = "<!-- m -->\nold", state = "closed" },
                    new { number = 12, body = "<!-- m -->\nduplicate", state = "open" },
                }
            });

        Assert.Equal(8, result.Plan.CanonicalIssueNumber);
        Assert.Equal(
            ["comment", "comment", "close", "reopen", "update", "comment"],
            result.Plan.Actions.Select(action => action.Type));
        Assert.Equal(
            result.Plan.Actions.Select(action => action.Type),
            result.AppliedActions.Select(action => action.Type));
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

    private sealed record RecordResult(int Number, bool Created, bool Skipped, int[] DuplicatesClosed);

    private sealed record IssueState(int Number, string State, string? StateReason, string Body, string[] Labels, string[] Comments);

    private sealed record PlanExecutionResult(ReconciliationPlan Plan, ReconciliationAction[] AppliedActions);

    private sealed record ReconciliationPlan(int? CanonicalIssueNumber, ReconciliationAction[] Actions);

    private sealed record ReconciliationAction(string Type);
}