// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class SyncMainToRelease14Tests(ITestOutputHelper output)
{
    [Fact]
    public void WorkflowRestrictsBotAccessToMainAndDoesNotExecuteBranchCode()
    {
        var root = LoadWorkflow();
        Assert.Empty(Mapping(root, "permissions").Children);
        var triggers = Mapping(root, "on");
        Assert.Equal(["schedule", "workflow_dispatch"], triggers.Children.Keys.Select(key => key.ToString()));
        var schedule = Assert.IsType<YamlMappingNode>(Assert.Single(Assert.IsType<YamlSequenceNode>(triggers.Children[new YamlScalarNode("schedule")])));
        Assert.Equal("23 8 * * *", Scalar(schedule, "cron"));
        var concurrency = Mapping(root, "concurrency");
        Assert.Equal("sync-main-to-release-14", Scalar(concurrency, "group"));
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));

        var job = Assert.IsType<YamlMappingNode>(Assert.Single(Mapping(root, "jobs").Children).Value);
        Assert.Equal("github.repository == 'microsoft/aspire' && github.ref == 'refs/heads/main'", Scalar(job, "if"));
        Assert.Equal("10", Scalar(job, "timeout-minutes"));
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        Assert.Equal(2, steps.Children.Count);
        var tokenStep = Assert.IsType<YamlMappingNode>(steps.Children[0]);
        Assert.Equal(["name", "id", "uses", "with"], tokenStep.Children.Keys.Select(key => key.ToString()));
        Assert.Matches("^actions/create-github-app-token@[a-f0-9]{40}$", Scalar(tokenStep, "uses"));
        var token = Mapping(tokenStep, "with");
        Assert.Equal(["client-id", "private-key", "owner", "repositories", "permission-contents", "permission-pull-requests", "skip-token-revoke"],
            token.Children.Keys.Select(key => key.ToString()));
        Assert.Equal("${{ secrets.ASPIRE_BOT_APP_ID }}", Scalar(token, "client-id"));
        Assert.Equal("${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}", Scalar(token, "private-key"));
        Assert.Equal("microsoft", Scalar(token, "owner"));
        Assert.Equal("aspire", Scalar(token, "repositories"));
        Assert.Equal("write", Scalar(token, "permission-contents"));
        Assert.Equal("write", Scalar(token, "permission-pull-requests"));
        Assert.Equal("false", Scalar(token, "skip-token-revoke"));

        var scriptStep = Assert.IsType<YamlMappingNode>(steps.Children[1]);
        Assert.Equal(["name", "uses", "env", "with"], scriptStep.Children.Keys.Select(key => key.ToString()));
        Assert.Matches("^actions/github-script@[a-f0-9]{40}$", Scalar(scriptStep, "uses"));
        Assert.Equal("${{ steps.app-token.outputs.app-slug }}[bot]", Scalar(Mapping(scriptStep, "env"), "SYNC_BOT_LOGIN"));
        var options = Mapping(scriptStep, "with");
        Assert.Equal("${{ steps.app-token.outputs.token }}", Scalar(options, "github-token"));
        Assert.Equal("0", Scalar(options, "retries"));
        Assert.Equal(-1, Scalar(options, "script").IndexOf("${{", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("up-to-date")]
    [InlineData("create")]
    [InlineData("existing")]
    [InlineData("orphaned-ref")]
    [InlineData("changed-ref")]
    [InlineData("multiple-prs")]
    [InlineData("conflict")]
    [InlineData("unknown-mergeability")]
    [InlineData("mergeability-ready-on-retry")]
    [InlineData("manual-dispatch")]
    [InlineData("ready")]
    [InlineData("merge-rejected")]
    [InlineData("merge-error")]
    [InlineData("merge-disabled")]
    [InlineData("auto-merge-disabled")]
    [InlineData("already-enabled")]
    [InlineData("squash-enabled")]
    [InlineData("closed")]
    [InlineData("draft")]
    [InlineData("retargeted")]
    [InlineData("wrong-author")]
    [InlineData("missing-marker")]
    [InlineData("fork-head")]
    [InlineData("changed-head-branch")]
    [InlineData("unrelated-pr")]
    [InlineData("wrong-repository")]
    [InlineData("untrusted-dispatch")]
    [InlineData("unsupported-event")]
    [InlineData("missing-bot")]
    [InlineData("list-error")]
    [InlineData("ref-read-error")]
    [InlineData("ref-write-error")]
    [InlineData("create-pr-error")]
    [InlineData("auto-merge-error")]
    [RequiresTools(["node"])]
    public async Task SynchronizationPreservesHistoryAndRespectsPolicy(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var job = Mapping(Mapping(LoadWorkflow(), "jobs"), "sync");
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        var script = Scalar(Mapping(Assert.IsType<YamlMappingNode>(steps.Children[1]), "with"), "script");
        var scriptPath = Path.Combine(workspace.Path, "sync-script.js");
        await File.WriteAllTextAsync(scriptPath, script);
        using var node = new NodeCommand(output, nameof(SyncMainToRelease14Tests))
            .WithTimeout(TimeSpan.FromMinutes(1));
        var result = await node.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "sync-main-to-release-14.harness.mjs"),
            scriptPath,
            scenario);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "sync-main-to-release-14.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
}
