// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Behavioral tests for .github/workflows/labeler-predict-pulls.js.
/// </summary>
public sealed class LabelerPredictPullsWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot = RepoRoot.Path;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public LabelerPredictPullsWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _harnessPath = Path.Combine(
            _repoRoot,
            "tests",
            "Infrastructure.Tests",
            "WorkflowScripts",
            "labeler-predict-pulls.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("Copilot")]
    [InlineData("copilot[bot]")]
    [InlineData("copilot-swe-agent")]
    [InlineData("copilot-swe-agent[bot]")]
    [InlineData("github-copilot[bot]")]
    [RequiresTools(["node"])]
    public async Task UnassignedCopilotPullRequestGetsNeedsAssigneeLabel(string author)
    {
        var result = await RunScriptAsync(PullRequest(author));

        Assert.Equal(["needs-assignee"], result.Labels);
        Assert.Equal(["pulls.get", "addLabels"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AssignedCopilotPullRequestLosesNeedsAssigneeLabel()
    {
        var result = await RunScriptAsync(
            PullRequest(
                author: "Copilot",
                assignees: [new { login = "ellahathaway", type = "User" }],
                labels: [new { name = "needs-assignee" }]));

        Assert.Empty(result.Labels);
        Assert.Equal(["pulls.get", "removeLabel"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ConcurrentPullRequestLabelRemovalIsIdempotent()
    {
        var result = await RunScriptAsync(
            PullRequest(
                author: "Copilot",
                assignees: [new { login = "ellahathaway", type = "User" }],
                labels: [new { name = "needs-assignee" }]),
            removeLabelNotFound: true);

        Assert.Empty(result.Labels);
        Assert.Equal(["pulls.get", "removeLabel"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClosedCopilotPullRequestLosesNeedsAssigneeLabel()
    {
        var result = await RunScriptAsync(
            PullRequest(
                author: "Copilot",
                state: "closed",
                labels: [new { name = "needs-assignee" }]));

        Assert.Empty(result.Labels);
        Assert.Equal(["pulls.get", "removeLabel"], result.Calls);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task NonCopilotPullRequestIsUnchanged()
    {
        var result = await RunScriptAsync(PullRequest(author: "outside-contributor"));

        Assert.Empty(result.Labels);
        Assert.Equal(["pulls.get"], result.Calls);
    }

    private static object PullRequest(
        string author,
        string state = "open",
        object[]? assignees = null,
        object[]? labels = null) => new
        {
            user = new { login = author },
            state,
            assignees = assignees ?? [],
            labels = labels ?? [],
        };

    private async Task<ScriptResult> RunScriptAsync(
        object pullRequest,
        bool removeLabelNotFound = false)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.result.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(
                new
                {
                    pullRequest,
                    removeLabelNotFound,
                },
                s_jsonOptions));

        using var command = new NodeCommand(_output, "labeler-predict-pulls");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath, outputPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse>(
            await File.ReadAllTextAsync(outputPath),
            s_jsonOptions);
        Assert.NotNull(response);

        return response.Result;
    }

    private sealed record HarnessResponse(ScriptResult Result);

    private sealed record ScriptResult(string[] Labels, string[] Calls);
}
