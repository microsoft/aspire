// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class OrganizationFundedCopilotReviewsTests(ITestOutputHelper output)
{
    [Fact]
    public void WorkflowKeepsPrivilegedExecutionMetadataOnly()
    {
        var root = LoadWorkflow();
        Assert.Empty(Mapping(root, "permissions").Children);
        var triggers = Mapping(root, "on");
        Assert.Equal(["pull_request_target", "schedule", "workflow_dispatch"], triggers.Children.Keys.Select(key => key.ToString()));
        var pullRequestTrigger = Mapping(triggers, "pull_request_target");
        Assert.Equal(["main", "release/**"], Sequence(pullRequestTrigger, "branches"));
        Assert.Equal(["opened", "synchronize"], Sequence(pullRequestTrigger, "types"));
        var schedule = Assert.IsType<YamlMappingNode>(Assert.Single(Assert.IsType<YamlSequenceNode>(triggers.Children[new YamlScalarNode("schedule")])));
        Assert.Equal("7,22,37,52 * * * *", Scalar(schedule, "cron"));

        var concurrency = Mapping(root, "concurrency");
        Assert.Equal("organization-funded-copilot-reviews", Scalar(concurrency, "group"));
        Assert.Equal("false", Scalar(concurrency, "cancel-in-progress"));
        var jobs = Mapping(root, "jobs");
        var job = Assert.IsType<YamlMappingNode>(Assert.Single(jobs.Children).Value);
        Assert.Equal(
            "github.repository == 'microsoft/aspire' && vars.COPILOT_REVIEW_MODE != 'disabled' && " +
            "(github.event_name != 'pull_request_target' || github.actor != 'dependabot[bot]') && " +
            "(github.event_name != 'workflow_dispatch' || github.ref == 'refs/heads/main')",
            Scalar(job, "if"));
        Assert.Equal("ubuntu-latest", Scalar(job, "runs-on"));
        Assert.Equal("10", Scalar(job, "timeout-minutes"));
        Assert.Empty(Mapping(job, "permissions").Children);

        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        Assert.Equal(2, steps.Children.Count);
        var tokenStep = Assert.IsType<YamlMappingNode>(steps.Children[0]);
        Assert.Equal(["name", "id", "uses", "with"], tokenStep.Children.Keys.Select(key => key.ToString()));
        Assert.Equal("app-token", Scalar(tokenStep, "id"));
        Assert.Matches("^actions/create-github-app-token@[a-f0-9]{40}$", Scalar(tokenStep, "uses"));
        var tokenOptions = Mapping(tokenStep, "with");
        Assert.Equal(
            ["client-id", "private-key", "owner", "repositories", "permission-pull-requests", "skip-token-revoke"],
            tokenOptions.Children.Keys.Select(key => key.ToString()));
        Assert.Equal("${{ secrets.ASPIRE_BOT_APP_ID }}", Scalar(tokenOptions, "client-id"));
        Assert.Equal("${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}", Scalar(tokenOptions, "private-key"));
        Assert.Equal("microsoft", Scalar(tokenOptions, "owner"));
        Assert.Equal("aspire", Scalar(tokenOptions, "repositories"));
        Assert.Equal("${{ vars.COPILOT_REVIEW_MODE == 'enabled' && 'write' || 'read' }}", Scalar(tokenOptions, "permission-pull-requests"));
        Assert.Equal("false", Scalar(tokenOptions, "skip-token-revoke"));

        var step = ScriptStep(root);
        Assert.Equal(["name", "uses", "env", "with"], step.Children.Keys.Select(key => key.ToString()));
        Assert.Matches("^actions/github-script@[a-f0-9]{40}$", Scalar(step, "uses"));
        var environment = Mapping(step, "env");
        Assert.Equal(["COPILOT_REVIEW_MODE", "COPILOT_REVIEW_PR_NUMBER"], environment.Children.Keys.Select(key => key.ToString()));
        Assert.Equal("${{ vars.COPILOT_REVIEW_MODE }}", Scalar(environment, "COPILOT_REVIEW_MODE"));
        Assert.Equal("${{ vars.COPILOT_REVIEW_PR_NUMBER }}", Scalar(environment, "COPILOT_REVIEW_PR_NUMBER"));
        var options = Mapping(step, "with");
        Assert.Equal(["github-token", "retries", "script"], options.Children.Keys.Select(key => key.ToString()));
        Assert.Equal("${{ steps.app-token.outputs.token }}", Scalar(options, "github-token"));
        Assert.Equal("0", Scalar(options, "retries"));
        Assert.Equal(-1, Scalar(options, "script").IndexOf("${{", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("default-dry-run")]
    [InlineData("explicit-dry-run")]
    [InlineData("disabled")]
    [InlineData("invalid-mode")]
    [InlineData("invalid-pilot")]
    [InlineData("unsafe-pilot")]
    [InlineData("pilot-other-pr")]
    [InlineData("pilot-scheduled")]
    [InlineData("scheduled-stale")]
    [InlineData("scheduled-stale-boundary")]
    [InlineData("scheduled-recent")]
    [InlineData("scheduled-stale-dry-run")]
    [InlineData("scheduled-stale-pilot")]
    [InlineData("scheduled-invalid-activity")]
    [InlineData("scheduled-missing-activity")]
    [InlineData("stale-push")]
    [InlineData("stale-manual")]
    [InlineData("external-author")]
    [InlineData("author-read")]
    [InlineData("author-none")]
    [InlineData("author-triage")]
    [InlineData("author-maintain")]
    [InlineData("author-admin")]
    [InlineData("author-custom-write")]
    [InlineData("author-unknown-permission")]
    [InlineData("author-missing")]
    [InlineData("author-permission-revoked")]
    [InlineData("external-author-maintainer-push")]
    [InlineData("external-author-scheduled")]
    [InlineData("external-author-manual")]
    [InlineData("external-author-dry-run")]
    [InlineData("api-permission-error")]
    [InlineData("copilot-author")]
    [InlineData("copilot-author-manual")]
    [InlineData("copilot-author-dry-run")]
    [InlineData("copilot-author-scan-continues")]
    [InlineData("copilot-login-human")]
    [InlineData("api-permission-not-found")]
    [InlineData("bot-author")]
    [InlineData("dependabot-scheduled")]
    [InlineData("dependabot-manual")]
    [InlineData("draft")]
    [InlineData("draft-scheduled")]
    [InlineData("closed")]
    [InlineData("unsupported-branch")]
    [InlineData("release-branch")]
    [InlineData("pending")]
    [InlineData("reviewed")]
    [InlineData("dismissed")]
    [InlineData("older-review")]
    [InlineData("human-review")]
    [InlineData("spoofed-reviewer")]
    [InlineData("head-changed")]
    [InlineData("closed-before-write")]
    [InlineData("draft-before-write")]
    [InlineData("retargeted-before-write")]
    [InlineData("pending-before-write")]
    [InlineData("metadata-injection")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-pr-repository")]
    [InlineData("wrong-number")]
    [InlineData("invalid-number")]
    [InlineData("invalid-sha")]
    [InlineData("missing-reviewers")]
    [InlineData("unsupported-event")]
    [InlineData("untrusted-dispatch")]
    [InlineData("manual-dispatch")]
    [InlineData("api-read-error")]
    [InlineData("api-history-error")]
    [InlineData("api-write-error")]
    [InlineData("pagination")]
    [InlineData("push-during-review")]
    [RequiresTools(["node"])]
    public async Task ReconciliationHonorsPolicy(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var script = Scalar(Mapping(ScriptStep(LoadWorkflow()), "with"), "script");
        var scriptPath = Path.Combine(workspace.Path, "review-script.js");
        await File.WriteAllTextAsync(scriptPath, script);

        using var node = new NodeCommand(output, nameof(OrganizationFundedCopilotReviewsTests))
            .WithTimeout(TimeSpan.FromMinutes(1));
        var result = await node.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "organization-funded-copilot-reviews.harness.mjs"),
            scriptPath,
            scenario);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static YamlMappingNode LoadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "organization-funded-copilot-reviews.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static YamlMappingNode ScriptStep(YamlMappingNode root)
    {
        var job = Mapping(Mapping(root, "jobs"), "request-reviews");
        var steps = Assert.IsType<YamlSequenceNode>(job.Children[new YamlScalarNode("steps")]);
        return Assert.IsType<YamlMappingNode>(steps.Children[1]);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;

    private static IEnumerable<string> Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]).Select(value => value.ToString());
}
