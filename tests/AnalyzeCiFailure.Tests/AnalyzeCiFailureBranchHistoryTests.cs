// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks successful-run search windows, branch selection and candidate-merge context.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("release/13.1")]
    [RequiresTools(["bash"])]
    public async Task HistoryQueriesTheConfiguredBranch(string branch)
    {
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            printf '%s\n' "$*" >> history-branch-calls.log
            if [[ "$*" != *"branch=$EXPECTED_BRANCH "* ]]; then
              printf '%s\n' 'Unexpected branch query' >&2
              exit 99
            fi
            printf '%s' '{"total_count":1,"workflow_runs":[{"id":12,"created_at":"2026-10-28T10:00:00Z","head_sha":"successful-sha"}]}'
            """);
        var github = new AnalysisGitHubClient(
            CreateGitHubClient(
            new Dictionary<string, string>
            {
                ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["EXPECTED_BRANCH"] = branch,
            }),
            CancellationToken.None);

        var run = await new BranchHistoryReader(github, "microsoft/aspire", branch)
            .FindLastSuccessfulRunAsync("1", "2026-10-28T11:00:00Z", failedId: 13);

        Assert.Equal(12, run["id"]!.GetValue<int>());
        Assert.Contains(
            $"-f branch={branch}",
            Assert.Single(await File.ReadAllLinesAsync(
            Path.Combine(_workspace.Path, "history-branch-calls.log"))),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("main", 1)]
    [InlineData("release/13.1", 2)]
    public void MergedPullRequestsMatchTheConfiguredBranch(string? branch, int expected)
    {
        var github = new AnalysisGitHubClient(CreateGitHubClient(), CancellationToken.None);
        var reader = branch is null ? new BranchHistoryReader(github, "microsoft/aspire")
            : new BranchHistoryReader(github, "microsoft/aspire", branch);
        var candidates = JsonNode.Parse("""
            [
              {"number":1,"base":{"ref":"main","repo":{"full_name":"microsoft/aspire"}},"merged_at":"2026-10-28T10:00:00Z"},
              {"number":2,"base":{"ref":"release/13.1","repo":{"full_name":"microsoft/aspire"}},"merged_at":"2026-10-28T10:00:00Z"},
              {"number":3,"base":{"ref":"release/13.1","repo":{"full_name":"other/aspire"}},"merged_at":"2026-10-28T10:00:00Z"},
              {"number":4,"base":{"ref":"release/13.1","repo":{"full_name":"microsoft/aspire"}},"merged_at":null}
            ]
            """)!.AsArray();

        var result = reader.FindMergedPullRequests(candidates);

        Assert.Equal(expected, Assert.Single(result)["number"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void HistoryRejectsAnEmptyBranch(string branch)
    {
        var github = new AnalysisGitHubClient(CreateGitHubClient(), CancellationToken.None);

        Assert.Throws<ValidationException>(() => new BranchHistoryReader(github, "microsoft/aspire", branch));
    }
}
