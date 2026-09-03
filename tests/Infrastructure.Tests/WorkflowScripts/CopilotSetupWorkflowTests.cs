// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class CopilotSetupWorkflowTests
{
    private const string WorkflowPath = ".github/workflows/copilot-setup-steps.yml";

    private static readonly YamlMappingNode s_workflow = LoadWorkflow();
    private static readonly YamlMappingNode s_job = Mapping(Mapping(s_workflow, "jobs"), "copilot-setup-steps");

    [Fact]
    public void CopilotSetupWorkflowSupportsManualAndPathScopedValidationTriggers()
    {
        var triggers = Mapping(s_workflow, "on");

        Assert.Equal(
            ["workflow_dispatch", "push", "pull_request"],
            triggers.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));

        Assert.Equal([WorkflowPath], SequenceScalars(Mapping(triggers, "push"), "paths"));
        Assert.Equal([WorkflowPath], SequenceScalars(Mapping(triggers, "pull_request"), "paths"));
    }

    [Fact]
    public void CopilotSetupWorkflowAddsDotnetPathsAsSeparateEntries()
    {
        var setupPath = Assert.Single(Steps(s_job), step => Scalar(step, "name") == "Setup PATH");
        var setupScript = Scalar(setupPath, "run");
        Assert.NotNull(setupScript);

        Assert.Equal(
            [
                "echo \"$HOME/.dotnet/tools\" >> $GITHUB_PATH",
                "echo \"$PWD/.dotnet/\" >> $GITHUB_PATH",
            ],
            setupScript!.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void CopilotSetupWorkflowKeepsExistingEnvironmentSetupBehavior()
    {
        Assert.Equal("${{ github.repository_owner == 'microsoft' && '8-core-ubuntu-latest' || 'ubuntu-latest' }}", Scalar(s_job, "runs-on"));

        var steps = Steps(s_job);
        Assert.Equal(
            [
                null,
                "Install gh-aw extension",
                "Restore solution",
                "Install verify tool",
                "Setup PATH",
                "Install .NET 10.x",
                "Install .NET 8.x",
                "dotnet --info",
            ],
            steps.Select(step => Scalar(step, "name")));

        Assert.Equal("./build.sh -restore || true", Scalar(Assert.Single(steps, step => Scalar(step, "name") == "Restore solution"), "run"));
        Assert.Equal("./dotnet.sh tool install --global Verify.Tool --version 0.6.0 || true", Scalar(Assert.Single(steps, step => Scalar(step, "name") == "Install verify tool"), "run"));
        Assert.Equal("v0.72.0", Scalar(Mapping(Assert.Single(steps, step => Scalar(step, "name") == "Install gh-aw extension"), "with"), "version"));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();

    private static List<string?> SequenceScalars(YamlMappingNode node, string key)
        => ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Cast<YamlScalarNode>().Select(item => item.Value).ToList();

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static YamlMappingNode LoadWorkflow()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(RepoPath(WorkflowPath)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static string RepoPath(params string[] path)
        => Path.Combine([RepoRoot.Path, .. path]);
}
