// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class DeploymentTestCommandTests(ITestOutputHelper output)
{
    [Fact]
    public void DeploymentStepsRequireRepositoryWriteAccess()
    {
        var steps = LoadSteps();
        Assert.Equal("check_permission", Scalar(steps[0], "id"));
        Assert.Equal(3, steps.Length);
        foreach (var step in steps.Skip(1))
        {
            Assert.Equal("steps.check_permission.outputs.has_write_access == 'true'", Scalar(step, "if"));
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("admin")]
    [InlineData("maintain")]
    [InlineData("read")]
    [InlineData("triage")]
    [InlineData("none")]
    [InlineData("unknown")]
    [InlineData("error-403")]
    [InlineData("error-404")]
    [InlineData("error-500")]
    [RequiresTools(["node"])]
    public async Task PermissionCheckOnlyAllowsRepositoryWriters(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var options = Assert.IsType<YamlMappingNode>(LoadSteps()[0].Children[new YamlScalarNode("with")]);
        var scriptPath = Path.Combine(workspace.Path, "permission-check.js");
        await File.WriteAllTextAsync(scriptPath, Scalar(options, "script"));
        using var node = new NodeCommand(output, nameof(DeploymentTestCommandTests))
            .WithTimeout(TimeSpan.FromMinutes(1));
        var result = await node.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "deployment-test-command.harness.mjs"),
            scriptPath,
            scenario);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static YamlMappingNode[] LoadSteps()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "deployment-test-command.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var job = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("deployment-test")]);
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        return steps.Children.Select(Assert.IsType<YamlMappingNode>).ToArray();
    }

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
}
