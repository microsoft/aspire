// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class GenerateApiDiffsWorkflowTests
{
    private const string PublishCondition = "${{ github.event_name == 'schedule' || (github.event_name == 'workflow_dispatch' && inputs.dry_run == false) }}";
    private const string WorkflowConcurrencyGroup = "${{ github.workflow }}-${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || github.run_id }}";
    private const string WorkflowCancelInProgressCondition = "${{ github.event_name == 'pull_request' }}";
    private const string TargetBranchMatrix = "${{ fromJSON(github.event_name == 'workflow_dispatch' && format('[{0}]', toJSON(inputs.target_branch)) || github.event_name == 'pull_request' && format('[{0}]', toJSON(github.base_ref)) || '[\"main\",\"release/13.6\"]') }}";
    private const string ConcurrencyGroup = "${{ format('{0}-{1}-{2}', github.workflow, matrix.target_branch, github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || (github.event_name == 'workflow_dispatch' && inputs.dry_run == true && 'dry-run') || 'publish') }}";
    private const string CancelInProgressCondition = "${{ github.event_name == 'pull_request' || (github.event_name == 'workflow_dispatch' && inputs.dry_run == true) }}";

    [Theory]
    [InlineData("generate-api-diffs.yml")]
    [InlineData("generate-ats-diffs.yml")]
    public void WorkflowRunsOnOwnChangesWithReadOnlyPermissions(string fileName)
    {
        var root = LoadWorkflow(fileName);
        var triggers = Mapping(root, "on");
        var pullRequest = Mapping(triggers, "pull_request");
        var paths = Sequence(pullRequest, "paths");

        Assert.Equal($".github/workflows/{fileName}", Scalar(Assert.Single(paths.Children)));

        var permissions = Mapping(root, "permissions");
        var permission = Assert.Single(permissions.Children);
        Assert.Equal("contents", Scalar(permission.Key));
        Assert.Equal("read", Scalar(permission.Value));
    }

    [Theory]
    [InlineData("generate-api-diffs.yml")]
    [InlineData("generate-ats-diffs.yml")]
    public void WorkflowDispatchDefaultsToDryRun(string fileName)
    {
        var root = LoadWorkflow(fileName);
        var inputs = Mapping(Mapping(Mapping(root, "on"), "workflow_dispatch"), "inputs");
        var dryRun = Mapping(inputs, "dry_run");

        Assert.Equal("true", Scalar(dryRun, "default"));
        Assert.Equal("boolean", Scalar(dryRun, "type"));
    }

    [Theory]
    [InlineData("generate-api-diffs.yml")]
    [InlineData("generate-ats-diffs.yml")]
    public void CheckoutUsesTrustedTargetAndConcurrencySeparatesPublishingFromDryRuns(string fileName)
    {
        var root = LoadWorkflow(fileName);
        var inputs = Mapping(Mapping(Mapping(root, "on"), "workflow_dispatch"), "inputs");
        var targetBranch = Mapping(inputs, "target_branch");
        var options = Sequence(targetBranch, "options").Children.Select(Scalar).ToList();
        var workflowConcurrency = Mapping(root, "concurrency");
        var job = Mapping(Mapping(root, "jobs"), "generate-and-pr");
        var matrix = Mapping(Mapping(job, "strategy"), "matrix");
        var concurrency = Mapping(job, "concurrency");
        var steps = Sequence(job, "steps").Children.Cast<YamlMappingNode>().ToList();
        var checkout = Assert.Single(steps, step => ScalarOrNull(step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);
        var appToken = Assert.Single(steps, step => ScalarOrNull(step, "name") == "Generate GitHub App Token");
        var createPullRequest = Assert.Single(steps, step => ScalarOrNull(step, "name") == "Create or update pull request");

        Assert.Equal("choice", Scalar(targetBranch, "type"));
        Assert.Equal(["main", "release/13.6"], options);
        Assert.Equal(WorkflowConcurrencyGroup, Scalar(workflowConcurrency, "group"));
        Assert.Equal(WorkflowCancelInProgressCondition, Scalar(workflowConcurrency, "cancel-in-progress"));
        Assert.Equal(TargetBranchMatrix, Scalar(matrix, "target_branch"));
        Assert.Equal(ConcurrencyGroup, Scalar(concurrency, "group"));
        Assert.Equal(CancelInProgressCondition, Scalar(concurrency, "cancel-in-progress"));
        Assert.Equal("${{ matrix.target_branch }}", Scalar(Mapping(checkout, "with"), "ref"));
        Assert.Equal("false", Scalar(Mapping(checkout, "with"), "persist-credentials"));
        Assert.Equal(PublishCondition, Scalar(appToken, "if"));
        Assert.Equal(PublishCondition, Scalar(createPullRequest, "if"));
    }

    private static YamlMappingNode LoadWorkflow(string fileName)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Scalar(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlNode node)
        => Assert.IsType<YamlScalarNode>(node).Value!;

    private static string? ScalarOrNull(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;
}
