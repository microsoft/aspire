// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class DeploymentTestRbacTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("grant")]
    [InlineData("rerun")]
    [InlineData("v2-token")]
    [InlineData("wrong-app")]
    [InlineData("wrong-tenant")]
    [InlineData("missing-oid")]
    [InlineData("invalid-token")]
    [InlineData("token-error")]
    [InlineData("assignment-error")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-principal")]
    [InlineData("wrong-role")]
    [InlineData("conditioned")]
    [InlineData("missing-subscription")]
    [RequiresTools(["node"])]
    public async Task GrantsFoundryAccessOnlyToConfiguredDeploymentPrincipal(string scenario)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "deployment-tests.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var job = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("provision-subscription")]);
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        var step = Assert.Single(steps.Children.Cast<YamlMappingNode>(), node =>
            node.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
            Assert.IsType<YamlScalarNode>(name).Value == "Ensure Foundry deployment permissions");
        Assert.Equal("true", Assert.IsType<YamlScalarNode>(step.Children[new YamlScalarNode("continue-on-error")]).Value);
        var options = Assert.IsType<YamlMappingNode>(step.Children[new YamlScalarNode("with")]);
        var source = Assert.IsType<YamlScalarNode>(options.Children[new YamlScalarNode("script")]).Value!;

        using var workspace = TemporaryWorkspace.Create(output);
        var scriptPath = Path.Combine(workspace.Path, "deployment-rbac.js");
        await File.WriteAllTextAsync(scriptPath, source);
        using var node = new NodeCommand(output, nameof(DeploymentTestRbacTests))
            .WithTimeout(TimeSpan.FromMinutes(1));
        var result = await node.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "deployment-test-rbac.harness.mjs"),
            scriptPath,
            scenario);
        Assert.True(result.ExitCode == 0, result.Output);
    }
}
