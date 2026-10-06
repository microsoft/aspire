// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class BackportWorkflowTests(ITestOutputHelper output)
{
    private const string WorkflowRelativePath = ".github/workflows/backport.yml";

    [Fact]
    public void JobsUseExactRepositoryOwnerMembership()
    {
        var jobs = Mapping(LoadWorkflow(), "jobs");

        Assert.Equal(
            "github.event_name == 'schedule' && contains(fromJSON('[\"dotnet\", \"microsoft\"]'), github.repository_owner)",
            Scalar(Mapping(jobs, "cleanup"), "if"));
        Assert.Equal(
            "github.event_name == 'issue_comment' && contains(fromJSON('[\"dotnet\", \"microsoft\"]'), github.repository_owner) && github.event.issue.pull_request != '' && contains(github.event.comment.body, '/backport to')",
            Scalar(Mapping(jobs, "backport"), "if"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CleanupCollectsWorkflowRunIdsBeforeDeleting()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var scriptPath = Path.Combine(workspace.Path, "cleanup-script.js");
        await File.WriteAllTextAsync(scriptPath, CleanupScript());

        using var node = new NodeCommand(output, nameof(BackportWorkflowTests))
            .WithTimeout(TimeSpan.FromMinutes(1));
        var result = await node.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "backport-cleanup.harness.mjs"),
            scriptPath);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static string CleanupScript()
    {
        var cleanup = Mapping(Mapping(LoadWorkflow(), "jobs"), "cleanup");
        var steps = Assert.IsType<YamlSequenceNode>(cleanup.Children[new YamlScalarNode("steps")]);
        var step = Assert.IsType<YamlMappingNode>(Assert.Single(steps));
        return Scalar(Mapping(step, "with"), "script");
    }

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, WorkflowRelativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
}
