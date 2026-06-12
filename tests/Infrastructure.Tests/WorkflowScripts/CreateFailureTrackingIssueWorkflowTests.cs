// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/create-failure-tracking-issue.js.
/// </summary>
public sealed class CreateFailureTrackingIssueWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public CreateFailureTrackingIssueWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = FindRepoRoot();
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "create-failure-tracking-issue.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildDedupQueryEscapesTitleAsPhrase()
    {
        var query = await InvokeHarnessAsync<string>(
            "buildDedupQuery",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "[Validate Published Build] 13.4.5 (release) failed"
            });

        Assert.Equal(
            "repo:microsoft/aspire is:issue is:open in:title \"[Validate Published Build] 13.4.5 (release) failed\"",
            query);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildDedupQueryEscapesEmbeddedQuotes()
    {
        var query = await InvokeHarnessAsync<string>(
            "buildDedupQuery",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "title with \"quotes\" inside"
            });

        Assert.Equal(
            "repo:microsoft/aspire is:issue is:open in:title \"title with \\\"quotes\\\" inside\"",
            query);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CreateOrComment_CreatesNewIssue_WhenSearchReturnsNothing()
    {
        var response = await InvokeHarnessAsync<SimulateResult>(
            "createOrCommentOnFailureIssue",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "[Validate Published Build] 13.4.5 (release) failed",
                body = "body content",
                labels = new[] { "area-cli", "failing-test" },
                runUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                searchResults = Array.Empty<object>()
            });

        Assert.Equal("created", response.Result.Action);
        Assert.Equal(42, response.Result.IssueNumber);
        Assert.Single(response.Calls.Create);
        Assert.Empty(response.Calls.Comment);

        var created = response.Calls.Create[0];
        Assert.Equal("[Validate Published Build] 13.4.5 (release) failed", created.Title);
        Assert.Equal(new[] { "area-cli", "failing-test" }, created.Labels);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CreateOrComment_CommentsOnExisting_WhenSearchReturnsExactMatch()
    {
        var response = await InvokeHarnessAsync<SimulateResult>(
            "createOrCommentOnFailureIssue",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "[Validate Published Build] 13.4.5 (release) failed",
                body = "body content",
                labels = new[] { "area-cli", "failing-test" },
                runUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                searchResults = new[]
                {
                    new
                    {
                        title = "[Validate Published Build] 13.4.5 (release) failed",
                        number = 7,
                        html_url = "https://github.com/microsoft/aspire/issues/7"
                    }
                }
            });

        Assert.Equal("commented", response.Result.Action);
        Assert.Equal(7, response.Result.IssueNumber);
        Assert.Single(response.Calls.Comment);
        Assert.Empty(response.Calls.Create);
        Assert.Contains("Another failure occurred", response.Calls.Comment[0].Body);
        Assert.Contains("https://github.com/microsoft/aspire/actions/runs/123", response.Calls.Comment[0].Body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CreateOrComment_RejectsSubstringTitleMatch_DefendsAgainstVersionPrefixCollision()
    {
        // Search may return tokenized matches (e.g. "13.4" hitting "13.4.1");
        // the helper must post-filter on exact title equality and treat the
        // fuzzy hit as not-found, creating a new issue instead.
        var response = await InvokeHarnessAsync<SimulateResult>(
            "createOrCommentOnFailureIssue",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "[Validate Published Build] 13.4 (release) failed",
                body = "body content",
                labels = new[] { "area-cli", "failing-test" },
                runUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                searchResults = new[]
                {
                    new
                    {
                        title = "[Validate Published Build] 13.4.1 (release) failed",
                        number = 5,
                        html_url = "https://github.com/microsoft/aspire/issues/5"
                    }
                }
            });

        Assert.Equal("created", response.Result.Action);
        Assert.Single(response.Calls.Create);
        Assert.Empty(response.Calls.Comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CreateOrComment_CreatesIssue_WhenSearchThrows()
    {
        var response = await InvokeHarnessAsync<SimulateResult>(
            "createOrCommentOnFailureIssue",
            new
            {
                owner = "microsoft",
                repo = "aspire",
                title = "[Validate Published Build] 13.4.5 (release) failed",
                body = "body content",
                labels = new[] { "area-cli", "failing-test" },
                runUrl = "https://github.com/microsoft/aspire/actions/runs/123",
                searchThrows = true
            });

        Assert.Equal("created", response.Result.Action);
        Assert.Single(response.Calls.Create);
        Assert.Single(response.Calls.Warnings);
        Assert.Contains("simulated search failure", response.Calls.Warnings[0]);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "create-failure-tracking-issue");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record HarnessResponse<T>(T Result);

    private sealed record SimulateResult(SimulateOutcome Result, CallLog Calls);

    private sealed record SimulateOutcome(string Action, string IssueUrl, int IssueNumber);

    private sealed record CallLog(SearchCall[] Search, CommentCall[] Comment, CreateCall[] Create, string[] Warnings);

    private sealed record SearchCall(string Q, int PerPage);

    private sealed record CommentCall(string Owner, string Repo, int IssueNumber, string Body);

    private sealed record CreateCall(string Owner, string Repo, string Title, string Body, string[] Labels);
}
