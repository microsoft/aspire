// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class SourceIndexPipelineTests
{
    [Fact]
    public void SourceIndexBuildUsesRepositoryWarningPolicy()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, "eng", "pipelines", "azure-pipelines.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var parameters = Mapping(Mapping(root, "extends"), "parameters");
        var stages = Assert.IsType<YamlSequenceNode>(parameters.Children[new YamlScalarNode("stages")]);
        var mainCondition = new YamlScalarNode("${{ if eq(variables['Build.SourceBranch'], 'refs/heads/main') }}");
        var conditionalStage = Assert.Single(stages.Children.OfType<YamlMappingNode>(),
            stage => stage.Children.ContainsKey(mainCondition));
        var mainStages = Assert.IsType<YamlSequenceNode>(conditionalStage.Children[mainCondition]);
        var sourceIndex = Assert.Single(mainStages.Children.OfType<YamlMappingNode>(),
            stage => Scalar(stage, "stage") == "source_index");
        var jobs = Assert.IsType<YamlSequenceNode>(sourceIndex.Children[new YamlScalarNode("jobs")]);
        var job = Assert.IsType<YamlMappingNode>(Assert.Single(jobs.Children));

        Assert.Equal("/eng/common/templates-official/jobs/jobs.yml@self", Scalar(job, "template"));
        var jobParameters = Mapping(job, "parameters");
        Assert.Equal("true", Scalar(jobParameters, "enableSourceIndex"));

        // BuildWarningPolicyTests covers the wrapper's forwarding behavior. Guard the
        // pipeline entry point too, since Arcade's default bypasses those exemptions.
        Assert.Equal(@".\build.cmd -restore -build -binarylog -ci",
            Scalar(Mapping(jobParameters, "sourceIndexParams"), "sourceIndexBuildCommand"));
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;
}
