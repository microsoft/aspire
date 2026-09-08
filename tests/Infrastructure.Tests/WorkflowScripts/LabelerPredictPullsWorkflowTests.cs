// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class LabelerPredictPullsWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot = RepoRoot.Path;
    private readonly string _harnessPath;
    private readonly string _script;
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
        _script = ExtractAssigneeLabelScript(LoadWorkflow());
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void WorkflowHandlesAssignmentLifecycleWithoutRerunningAreaPrediction()
    {
        var workflow = LoadWorkflow();
        var root = Assert.IsType<YamlMappingNode>(workflow.Documents[0].RootNode);
        var trigger = Mapping(Mapping(root, "on"), "pull_request_target");
        var types = Sequence(trigger, "types").Children.Cast<YamlScalarNode>().Select(node => node.Value);

        Assert.Equal(["opened", "assigned", "unassigned"], types);

        var jobs = Mapping(root, "jobs");
        var updateJob = Mapping(jobs, "update-copilot-assignee-label");
        Assert.Equal(
            "${{ github.event_name == 'pull_request_target' && github.repository_owner == 'microsoft' }}",
            Scalar(updateJob, "if"));
        var concurrency = Mapping(updateJob, "concurrency");
        Assert.Equal(
            "copilot-assignee-label-${{ github.repository }}-${{ github.event.pull_request.number || github.run_id }}",
            Scalar(concurrency, "group"));
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));
        Assert.Equal("write", Scalar(Mapping(updateJob, "permissions"), "pull-requests"));

        var updateStep = Assert.Single(
            Sequence(updateJob, "steps").Children.Cast<YamlMappingNode>(),
            step => Scalar(step, "name") == "Update Copilot PR assignee label");
        Assert.Equal(
            "actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3",
            Scalar(updateStep, "uses"));

        var predictionJob = Mapping(jobs, "predict-pull-label");
        Assert.Contains(
            "github.event.action == 'opened'",
            Scalar(predictionJob, "if"),
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UnassignedCopilotPullRequestGetsNeedsAssigneeLabel()
    {
        var result = await RunScriptAsync(PullRequest(author: "Copilot"));

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
                    script = _script,
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

    private YamlStream LoadWorkflow()
    {
        var workflow = new YamlStream();
        using var reader = File.OpenText(
            Path.Combine(_repoRoot, ".github", "workflows", "labeler-predict-pulls.yml"));
        workflow.Load(reader);

        return workflow;
    }

    private static string ExtractAssigneeLabelScript(YamlStream workflow)
    {
        var root = Assert.IsType<YamlMappingNode>(workflow.Documents[0].RootNode);
        var job = Mapping(Mapping(root, "jobs"), "update-copilot-assignee-label");
        var step = Assert.Single(
            Sequence(job, "steps").Children.Cast<YamlMappingNode>(),
            candidate => Scalar(candidate, "name") == "Update Copilot PR assignee label");

        return Scalar(Mapping(step, "with"), "script");
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;

    private sealed record HarnessResponse(ScriptResult Result);

    private sealed record ScriptResult(string[] Labels, string[] Calls);
}
