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
    public async Task PublishPrefersClosedCanonicalMarkerOverOpenNewerRootAlias()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = new
                {
                    id = "oldest-sample-test",
                    type = "flaky-test",
                    title = "Oldest sample test",
                    test_name = "Aspire.Sample.Tests.SampleTests.FlakyTest",
                    error_pattern = "The sample test failed.",
                    aliases = new[] { "newer-sample-test" },
                    job_ids = new[] { 101 },
                    job_names = new[] { "Tests / Sample" }
                },
                issues = new object[]
                {
                    new
                    {
                        number = 10,
                        state = "open",
                        body = "<!-- ci-failure-cause:newer-sample-test -->\n<!-- ci-failure-cause-type:flaky-test -->\n"
                    },
                    new
                    {
                        number = 20,
                        state = "closed",
                        body = "<!-- ci-failure-cause:oldest-sample-test -->\n<!-- ci-failure-cause-type:flaky-test -->\n"
                    }
                }
            });

        Assert.Equal(20, result.Publish.Number);
        Assert.Equal([10], result.Publish.DuplicatesClosed);
        Assert.Equal("open", Assert.Single(result.Issues, issue => issue.Number == 20).State);
        Assert.Equal("closed", Assert.Single(result.Issues, issue => issue.Number == 10).State);
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/20",
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
    public async Task ReplayingTrimmedOccurrenceDoesNotReopenClosedIssue()
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
                    occurrences = new object[]
                    {
                        new { run_id = 991, observed_at = "2026-08-27T00:00:00Z" },
                        new { run_id = 100, observed_at = "2026-08-28T00:00:00Z" },
                        new { run_id = 200, observed_at = "2026-08-29T00:00:00Z" },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 2 most recent of 3 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-28 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            | 2026-08-29 | [200](https://github.com/microsoft/aspire/actions/runs/200) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        Assert.True(result.Publish.Skipped);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.DoesNotContain("[991](", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("update", result.Calls);
        var occurrence = Assert.Single(
            result.StoredCause.GetProperty("occurrences").EnumerateArray(),
            occurrence => occurrence.GetProperty("run_id").GetInt64() == 991);
        Assert.True(occurrence.GetProperty("issue_published").GetBoolean());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReplayingPersistedOccurrenceRetriesWhenPriorIssueUpdateDidNotComplete()
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
                    occurrences = new object[]
                    {
                        new { run_id = 100, observed_at = "2026-08-27T00:00:00Z" },
                        new { run_id = 200, observed_at = "2026-08-28T00:00:00Z" },
                        new { run_id = 991, observed_at = "2026-08-29T18:30:00Z" },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 2 most recent of 2 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-27 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            | 2026-08-28 | [200](https://github.com/microsoft/aspire/actions/runs/200) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        Assert.False(result.Publish.Skipped);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("open", issue.State);
        Assert.Contains("[991](", issue.Body, StringComparison.Ordinal);
        Assert.Contains("update", result.Calls);
        var occurrence = Assert.Single(
            result.StoredCause.GetProperty("occurrences").EnumerateArray(),
            occurrence => occurrence.GetProperty("run_id").GetInt64() == 991);
        Assert.True(occurrence.GetProperty("issue_published").GetBoolean());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReplayingOccurrenceWithPublicationReceiptDoesNotReopenClosedIssue()
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
                    occurrences = new object[]
                    {
                        new { run_id = 100, observed_at = "2026-08-27T00:00:00Z" },
                        new { run_id = 200, observed_at = "2026-08-28T00:00:00Z" },
                        new
                        {
                            run_id = 991,
                            observed_at = "2026-08-29T18:30:00Z",
                            issue_published = true
                        },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 2 most recent of 2 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-27 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            | 2026-08-28 | [200](https://github.com/microsoft/aspire/actions/runs/200) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        Assert.True(result.Publish.Skipped);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.DoesNotContain("[991](", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("update", result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReplayingOccurrenceWithChainedAliasPublicationReceiptDoesNotReopenClosedIssue()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = new
                {
                    id = "worker-crash",
                    aliases = new[] { "intermediate-worker-crash", "legacy-worker-crash" },
                    type = "infra-failure",
                    title = "Worker process crashed",
                    error_pattern = "Process completed with exit code -1073741502 (0xC0000142)",
                    job_names = new[] { "Build / Windows", "Tests / Windows" }
                },
                storedCause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    occurrences = new object[]
                    {
                        new { run_id = 100, observed_at = "2026-08-28T00:00:00Z" },
                        new { run_id = 991, observed_at = "2026-08-29T18:30:00Z" },
                    }
                },
                storedAliases = new object[]
                {
                    new
                    {
                        id = "intermediate-worker-crash",
                        canonical_id = "worker-crash",
                        type = "infra-failure"
                    },
                    new
                    {
                        id = "legacy-worker-crash",
                        canonical_id = "intermediate-worker-crash",
                        type = "infra-failure",
                        occurrences = new object[]
                        {
                            new
                            {
                                run_id = 991,
                                observed_at = "2026-08-27T00:00:00Z",
                                issue_published = true
                            },
                        }
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "closed",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 1 most recent of 1 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-28 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        Assert.True(result.Publish.Skipped);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("closed", issue.State);
        Assert.DoesNotContain("[991](", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("update", result.Calls);
        var occurrence = Assert.Single(
            result.StoredCause.GetProperty("occurrences").EnumerateArray(),
            occurrence => occurrence.GetProperty("run_id").GetInt64() == 991);
        Assert.True(occurrence.GetProperty("issue_published").GetBoolean());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishUpdatesManagedOccurrenceSectionAndPreservesTotalCount()
    {
        var cause = CreateCause();
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause,
                storedCause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    occurrences = new object[]
                    {
                        new { run_id = 100, observed_at = "2026-08-27T00:00:00Z" },
                        new { run_id = 200, observed_at = "2026-08-28T00:00:00Z" },
                        new { run_id = 991, observed_at = "2026-08-29T18:30:00Z" },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "open",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            Preserve this issue description.

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 2 most recent of 2 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-27 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            | 2026-08-28 | [200](https://github.com/microsoft/aspire/actions/runs/200) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        var body = Assert.Single(result.Issues).Body;
        Assert.Contains("Preserve this issue description.", body, StringComparison.Ordinal);
        Assert.Contains("Showing 3 most recent of 3 occurrences.", body, StringComparison.Ordinal);
        Assert.Contains("[100](", body, StringComparison.Ordinal);
        Assert.Contains("[200](", body, StringComparison.Ordinal);
        Assert.Contains("[991](", body, StringComparison.Ordinal);
        Assert.EndsWith("<!-- ci-failure-occurrences:end -->\n", body, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishTrimsOldestOccurrencesToStayWithinBodyBudget()
    {
        var cause = CreateCause();
        var largePrefix = $"""
            <!-- ci-failure-cause:worker-crash -->
            <!-- ci-failure-cause-type:infra-failure -->

            {new string('x', 64_400)}

            """;
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause,
                storedCause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    occurrences = new object[]
                    {
                        new { run_id = 100, observed_at = "2026-08-27T00:00:00Z" },
                        new { run_id = 200, observed_at = "2026-08-28T00:00:00Z" },
                        new { run_id = 991, observed_at = "2026-08-29T18:30:00Z" },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "open",
                        body = largePrefix + """
                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 2 most recent of 2 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            | 2026-08-27 | [100](https://github.com/microsoft/aspire/actions/runs/100) | ` Build / Windows ` | #19804 |
                            | 2026-08-28 | [200](https://github.com/microsoft/aspire/actions/runs/200) | ` Build / Windows ` | #19804 |
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        var body = Assert.Single(result.Issues).Body;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) <= 65_000);
        Assert.DoesNotContain("[100](", body, StringComparison.Ordinal);
        Assert.Contains("[991](", body, StringComparison.Ordinal);
        Assert.Contains("most recent of 3 occurrences.", body, StringComparison.Ordinal);
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
    public async Task PublishTreatsCauseFieldsAsLiteralMarkdown()
    {
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause = new
                {
                    id = "worker-crash",
                    type = "flaky-test",
                    title = "Worker `crash`",
                    test_name = "Tests.`Flaky`",
                    error_pattern = "Failure\n```\n# heading",
                    job_names = new[] { "Build `Windows`" }
                },
                issues = Array.Empty<object>()
            });

        var body = Assert.Single(result.Issues).Body;
        Assert.DoesNotContain("\n```\n", body, StringComparison.Ordinal);
        Assert.Contains("    Failure\n    ```\n    # heading", body, StringComparison.Ordinal);
        Assert.Contains("`` Tests.`Flaky` ``", body, StringComparison.Ordinal);
        Assert.Contains("`` Worker `crash` ``", body, StringComparison.Ordinal);
        Assert.Contains("`` Build `Windows` ``", body, StringComparison.Ordinal);
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
                    candidateHistoryState = "available",
                    triggeringMerge = new { number = 42, title = "Improve `CI`" }
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
        Assert.Contains(
            "Triggering merge PR (context only, not necessarily causal): #42 `` Improve `CI` ``",
            issue.Body,
            StringComparison.Ordinal);
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/1000",
            result.StoredCause.GetProperty("issue_url").GetString());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unavailable")]
    [RequiresTools(["node"])]
    public async Task PublishDoesNotExposeMainAttributionWithoutCompleteHistory(string candidateHistoryState)
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
                    candidateHistoryState,
                    triggeringMerge = new { number = 42, title = "Must not be rendered" }
                }
            });

        Assert.DoesNotContain("Triggering merge PR", Assert.Single(result.Issues).Body, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishUpdatesExistingIssueWhenFreshBodyExceedsPublicationBudget()
    {
        var cause = new
        {
            id = "worker-crash",
            type = "infra-failure",
            title = "Worker process crashed",
            error_pattern = new string('x', 65_000),
            job_names = new[] { "Build / Windows" }
        };
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause,
                storedCause = new
                {
                    id = "worker-crash",
                    type = "infra-failure",
                    occurrences = new object[]
                    {
                        new { run_id = 991, observed_at = "2026-08-29T18:30:00Z" },
                    }
                },
                issues = new[]
                {
                    new
                    {
                        number = 10,
                        state = "open",
                        body = """
                            <!-- ci-failure-cause:worker-crash -->
                            <!-- ci-failure-cause-type:infra-failure -->

                            Preserve this issue description.

                            <!-- ci-failure-occurrences:start -->
                            ## Occurrences

                            Showing 0 most recent of 0 occurrences.

                            | Date | Build | Job | Context |
                            |------|-------|-----|----|
                            <!-- ci-failure-occurrences:end -->
                            """
                    }
                }
            });

        Assert.Equal(10, result.Publish.Number);
        var body = Assert.Single(result.Issues).Body;
        Assert.Contains("Preserve this issue description.", body, StringComparison.Ordinal);
        Assert.Contains("[991](", body, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) <= 65_000);
        Assert.Contains("update", result.Calls);
        Assert.DoesNotContain("create", result.Calls);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("Skipping issue creation.", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublishSkipsNewIssueWhenRenderedBodyExceedsPublicationBudget()
    {
        var cause = new
        {
            id = "worker-crash",
            type = "infra-failure",
            title = "Worker process crashed",
            error_pattern = new string('x', 65_000),
            job_names = new[] { "Build / Windows" }
        };
        var result = await InvokeHarnessAsync<PublishResult>(
            "publishCauseIssues",
            new
            {
                workspace = _workspace.Path,
                cause,
                storedCause = cause,
                issues = Array.Empty<object>()
            });

        Assert.Null(result.Publish);
        Assert.Empty(result.Issues);
        Assert.DoesNotContain("create", result.Calls);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains(
                "Cause issue body exceeds the 65000-byte publication budget. Skipping issue creation.",
                StringComparison.Ordinal));
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
        string[] Warnings,
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
