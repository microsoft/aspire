// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class DeploymentTestCommandTests(ITestOutputHelper output)
{
    [Fact]
    public void CommandPrefilterSelectsCandidatesOnMicrosoftPullRequests()
    {
        var condition = Scalar(LoadJob(), "if");
        Assert.Equal(
            "${{ startsWith(github.event.comment.body, '/deployment-test') && github.event.issue.pull_request && github.repository_owner == 'microsoft' }}",
            string.Join(" ", condition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

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
        => await RunPermissionCheckAsync(scenario, "/deployment-test", validCommand: true);

    [Theory]
    [InlineData("/deployment-test", true)]
    [InlineData("/deployment-test ", true)]
    [InlineData("/deployment-test arguments", true)]
    [InlineData("/deployment-test\n", true)]
    [InlineData("/deployment-test\r", true)]
    [InlineData("/deployment-test\r\nMore text", true)]
    [InlineData("/deployment-test\targuments", true)]
    [InlineData("/DEPLOYMENT-TEST\nMore text", true)]
    [InlineData("/deployment-testing", false)]
    [InlineData("/deployment-test-disabled", false)]
    [InlineData("/deployment-test/extra", false)]
    [InlineData("/deployment-test.", false)]
    [InlineData(" /deployment-test", false)]
    [InlineData("Please run /deployment-test", false)]
    [InlineData("", false)]
    [RequiresTools(["node"])]
    public async Task CommandRequiresEndOfTextOrWhitespace(string body, bool validCommand)
        => await RunPermissionCheckAsync("write", body, validCommand);

    private async Task RunPermissionCheckAsync(string scenario, string body, bool validCommand)
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
            scenario,
            body,
            validCommand ? "true" : "false");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static YamlMappingNode[] LoadSteps()
    {
        var steps = Assert.IsType<YamlSequenceNode>(LoadJob().Children[new YamlScalarNode("steps")]);
        return steps.Children.Select(Assert.IsType<YamlMappingNode>).ToArray();
    }

    private static YamlMappingNode LoadJob()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "deployment-test-command.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        return Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("deployment-test")]);
    }

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
}
