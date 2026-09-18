// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class CopilotCodeReviewWorkflowTests
{
    private static readonly YamlMappingNode s_workflow = LoadWorkflow();
    private static readonly YamlMappingNode s_job = Mapping(Mapping(s_workflow, "jobs"), "copilot-setup-steps");

    [Fact]
    public void UsesRequiredCopilotSetupStepsJobOnCurrentRunner()
    {
        Assert.Equal("Copilot Code Review Setup", Scalar(s_workflow, "name"));
        Assert.Equal("${{ github.repository_owner == 'microsoft' && '8-core-ubuntu-latest' || 'ubuntu-latest' }}", Scalar(s_job, "runs-on"));
        Assert.Empty(Mapping(s_job, "permissions").Children);
    }

    [Fact]
    public void SupportsManualAndChangeValidation()
    {
        var triggers = Mapping(s_workflow, "on");

        Assert.True(triggers.Children.ContainsKey(new YamlScalarNode("workflow_dispatch")));

        var pullRequest = Mapping(triggers, "pull_request");
        Assert.Equal([".github/workflows/copilot-code-review.yml"], SequenceScalars(pullRequest, "paths"));

        var push = Mapping(triggers, "push");
        Assert.Equal(["main"], SequenceScalars(push, "branches"));
        Assert.Equal([".github/workflows/copilot-code-review.yml"], SequenceScalars(push, "paths"));
    }

    [Fact]
    public void DoesNotInstallCodingAgentSetupDependencies()
    {
        var step = Assert.Single(Steps(s_job));

        Assert.Equal("Use default Copilot code review environment", Scalar(step, "name"));
        Assert.Equal("echo \"No dedicated setup is required before Copilot code review.\"", Scalar(step, "run"));
        Assert.False(step.Children.ContainsKey(new YamlScalarNode("uses")));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode node)
        => ((YamlSequenceNode)node.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();

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
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "copilot-code-review.yml")));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
