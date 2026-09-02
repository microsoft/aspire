// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureCauseIssuesTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public AnalyzeCiFailureCauseIssuesTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(
            _repoRoot,
            "tests",
            "Infrastructure.Tests",
            "WorkflowScripts",
            "analyze-ci-failure-cause-issues.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task ExactTypeMarkerCannotBeOverriddenByLegacyTypeText()
    {
        var result = await InvokeHarnessAsync<bool>(
            "matchesCauseIssue",
            new
            {
                cause = CreateCause(),
                issue = new
                {
                    number = 12,
                    body = """
                        <!-- ci-failure-cause:worker-crash -->
                        <!-- ci-failure-cause-type:flaky-test -->

                        **Type**: infra-failure
                        """
                }
            });

        Assert.False(result);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishUsesOldestExactTypedIssueAndClosesDuplicate()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = CreateCause(),
                storedCause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    issue_url = "https://github.com/microsoft/aspire/issues/20"
                },
                issues = new object[]
                {
                    new
                    {
                        number = 20,
                        state = "open",
                        body = "<!-- ci-failure-cause:worker-crash -->\n<!-- ci-failure-cause-type:infra-failure -->\n"
                    },
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            ## Occurrences

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-01 | [100](https://github.com/microsoft/aspire/actions/runs/100) | Build / Windows | #19804 |

                            """
                    },
                    new
                    {
                        number = 5,
                        state = "open",
                        body = "<!-- ci-failure-cause:worker-crash -->\n<!-- ci-failure-cause-type:flaky-test -->\n**Type**: infra-failure"
                    },
                },
                repeat = 2
            });

        Assert.Equal(10, result.Publish.Number);
        Assert.Equal([20], result.Publish.DuplicatesClosed);

        var canonical = Assert.Single(result.Issues, issue => issue.Number == 10);
        Assert.Equal("open", canonical.State);
        Assert.Equal(1, canonical.Body.Split("[991](", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("|\n\n|", canonical.Body, StringComparison.Ordinal);
        Assert.Single(canonical.Comments);

        var duplicate = Assert.Single(result.Issues, issue => issue.Number == 20);
        Assert.Equal("closed", duplicate.State);
        Assert.Equal("not_planned", duplicate.StateReason);
        Assert.Single(duplicate.Comments);
        Assert.Contains("listComments", result.Calls);

        var wrongType = Assert.Single(result.Issues, issue => issue.Number == 5);
        Assert.Equal("open", wrongType.State);

        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/10",
            result.StoredCause.GetProperty("issue_url").GetString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReplayingExistingOccurrenceDoesNotReopenClosedIssue()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = CreateCause(),
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            ## Occurrences

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-29 | [991](https://github.com/microsoft/aspire/actions/runs/991) | Build / Windows | #19804 |

                            """
                    }
                }
            });

        Assert.True(result.Publish.Skipped);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.Equal(1, issue.Body.Split("[991](", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("update", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishReusesIssueWithCanonicalCauseAlias()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = new
                {
                    id = "worker-crash",
                    aliases = new[] { "Legacy.Worker_Crash" },
                    type = "infra-failure",
                    title = "Worker process crashed",
                    error_pattern = "Process completed with exit code -1073741502 (0xC0000142)",
                    job_names = new[] { "Build / Windows" }
                },
                issues = new object[]
                {
                    new
                    {
                        number = 12,
                        state = "open",
                        body = "<!-- ci-failure-cause:Legacy.Worker_Crash -->\n<!-- ci-failure-cause-type:infra-failure -->\n"
                    }
                }
            });

        Assert.Equal(12, result.Publish.Number);
        Assert.Single(result.Issues);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task OccurrenceRowsEscapeMarkdownTablePipes()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    title = "Worker process crashed",
                    error_pattern = "Worker crashed",
                    job_names = new[] { "Build | Windows" }
                },
                issues = Array.Empty<object>()
            });

        Assert.Contains("Build \\| Windows", Assert.Single(result.Issues).Body, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishDoesNotRenderUnavailablePrAsNumber()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = CreateCause(),
                issues = Array.Empty<object>(),
                prNumber = 0
            });

        var issue = Assert.Single(result.Issues);
        Assert.Contains("| unavailable |", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Pull request: #0", issue.Body, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishCreatesMainBreakageIssueFromTrustedContext()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = CreateCause(type: "main-repository-breakage"),
                issues = Array.Empty<object>(),
                runScope = "main",
                mainContext = new
                {
                    lastSuccessfulSha = "1111111111111111111111111111111111111111",
                    failedSha = "2222222222222222222222222222222222222222",
                    triggeringMerge = "#42 Improve CI"
                }
            });

        var issue = Assert.Single(result.Issues);
        Assert.Equal("[Main CI Failure] Worker process crashed", issue.Title);
        Assert.Equal(["ci-failure-cause", "main-ci-break"], issue.Labels);
        Assert.StartsWith(
            "<!-- ci-failure-cause:worker-crash -->\n<!-- ci-failure-cause-type:main-repository-breakage -->\n",
            issue.Body,
            StringComparison.Ordinal);
        Assert.Contains("Last successful main SHA: `1111111111111111111111111111111111111111`", issue.Body, StringComparison.Ordinal);
        Assert.Contains("Failed main SHA: `2222222222222222222222222222222222222222`", issue.Body, StringComparison.Ordinal);
        Assert.Contains("Triggering merge PR (context only, not necessarily causal): #42 Improve CI", issue.Body, StringComparison.Ordinal);
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/1000",
            result.StoredCause.GetProperty("issue_url").GetString());
    }

    [Fact]
    public void AdapterDelegatesLifecyclePlanningAndExecutionToTrackingIssueEngine()
    {
        var sourcePath = Path.Combine(_repoRoot, ".github", "workflows", "analyze-ci-failure-cause-issues.js");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("tracking.executeIssueReconciliation(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("github.rest.issues", source, StringComparison.Ordinal);
        Assert.DoesNotContain("github.paginate", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".sort(", source, StringComparison.Ordinal);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "analyze-ci-failure-cause-issues");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private static object CreateCause(string type = "infra-failure")
        => new
        {
            id = "worker-crash",
            type,
            title = "Worker process crashed",
            error_pattern = "Process completed with exit code -1073741502 (0xC0000142)",
            job_names = new[] { "Build / Windows", "Tests / Windows" }
        };

    private sealed record HarnessResponse<T>(T Result);

    private sealed record PublishResult(
        PublishSummary Publish,
        string[] Calls,
        IssueState[] Issues,
        JsonElement StoredCause);

    private sealed record PublishSummary(int Number, bool Created, bool Skipped, int[] DuplicatesClosed);

    private sealed record IssueState(
        int Number,
        string State,
        string? StateReason,
        string Title,
        string Body,
        string[] Labels,
        string[] Comments);
}
