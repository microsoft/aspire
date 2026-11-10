// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class AnalyzeCiFailureReportTemplatesTests(ITestOutputHelper output)
{
    [Theory]
    [RequiresTools(["node"])]
    [InlineData("automatic", "workflow_run", "40727814372")]
    [InlineData("manual", "workflow_dispatch", "9223372036854775807")]
    [InlineData("zero", "workflow_dispatch", "0")]
    [InlineData("leading-zero", "workflow_dispatch", "01")]
    [InlineData("out-of-range", "workflow_dispatch", "9223372036854775808")]
    [InlineData("injection", "workflow_dispatch", "1)\n::error::injected")]
    public async Task OperationalTemplatesIncludeTrustedSourceContext(string scenario, string eventName, string sourceRunId)
    {
        var result = await RunAsync(eventName, sourceRunId);

        Assert.True(result["installed"]!.GetValue<bool>());
        Assert.True(result["originalsUnchanged"]!.GetValue<bool>());
        Assert.Equal(scenario is "automatic" or "manual" ? 0 : 1, result["warnings"]!.AsArray().Count);
        await Verifier.Verify(result["templates"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseDirectory("Snapshots")
            .UseParameters(scenario);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("agent_failure_issue.md")]
    [InlineData("failed_jobs_issue.md")]
    public async Task MissingPinnedTemplateDoesNotSelectPartialOverrides(string missingTemplate)
    {
        var result = await RunAsync("workflow_run", "40727814372", missingTemplate);

        Assert.False(result["installed"]!.GetValue<bool>());
        Assert.True(result["originalsUnchanged"]!.GetValue<bool>());
        Assert.Contains(missingTemplate, result["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("SOURCE_EVENT", "pull_request")]
    [InlineData("SOURCE_RUN_ID", "0")]
    [InlineData("GITHUB_REPOSITORY", "microsoft/aspire)\nmalformed")]
    [InlineData("GITHUB_SERVER_URL", "https://github.com/other")]
    [InlineData("GITHUB_SERVER_URL", "https://user:password@github.com")]
    public async Task InvalidTrustedContextDoesNotSelectOverrides(string key, string value)
    {
        var result = await RunAsync(
            "workflow_run",
            "40727814372",
            overrides: new JsonObject { [key] = value });

        Assert.False(result["installed"]!.GetValue<bool>());
        Assert.True(result["originalsUnchanged"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(result["error"]!.GetValue<string>()));
    }

    private async Task<JsonNode> RunAsync(
        string eventName,
        string sourceRunId,
        string? missingTemplate = null,
        JsonObject? overrides = null)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var inputs = Path.Combine(workspace.Path, "inputs.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(new { sourceRunId, overrides }));
        var script = """
            const fs = require('node:fs');
            const path = require('node:path');
            const { prepareReportTemplates } = require(process.argv[1]);
            const root = process.argv[2];
            const eventName = process.argv[3];
            const input = JSON.parse(fs.readFileSync(process.argv[4], 'utf8'));
            const sourceId = input.sourceRunId;
            const missing = process.argv[5];
            const names = ['agent_failure_issue.md', 'agent_failure_comment.md', 'failed_jobs_issue.md',
              'missing_data_issue.md', 'missing_tool_issue.md'];
            const prompts = path.join(root, 'gh-aw', 'prompts');
            fs.mkdirSync(prompts, {recursive: true});
            const original = 'Upstream report for {workflow_name}.\n\n[Analyzer run]({run_url})\n';
            for (const name of names) if (name !== missing) fs.writeFileSync(path.join(prompts, name), original);
            fs.writeFileSync(path.join(prompts, 'other.md'), 'Unrelated template\n');
            const githubEnv = path.join(root, 'github-env');
            fs.writeFileSync(githubEnv, '');
            const warnings = [];
            console.warn = message => warnings.push(message);
            let error;
            try {
              prepareReportTemplates({
                RUNNER_TEMP: root, GITHUB_ENV: githubEnv, GITHUB_REPOSITORY: 'microsoft/aspire',
                GITHUB_SERVER_URL: 'https://github.com', SOURCE_EVENT: eventName, SOURCE_RUN_ID: sourceId,
                ...input.overrides
              });
            } catch (e) { error = e.message; }
            const envText = fs.readFileSync(githubEnv, 'utf8');
            const selected = envText.match(/^GH_AW_PROMPTS_DIR=(.+)$/m)?.[1].trim();
            const templates = {};
            if (selected) for (const name of [...names, 'other.md']) {
              templates[name] = fs.readFileSync(path.join(selected, name), 'utf8').replace(/\r\n/g, '\n');
            }
            console.log(JSON.stringify({
              installed: !!selected, templates, error, warnings,
              originalsUnchanged: names.filter(name => name !== missing).every(name =>
                fs.readFileSync(path.join(prompts, name), 'utf8') === original)
            }));
            """;
        using var command = new NodeCommand(output, label: "analyzer-report-templates")
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromSeconds(30));
        var result = await command.ExecuteScriptAsync(
            "-e",
            script,
            Path.Combine(RepoRoot.Path, ".github", "workflows", "analyze-ci-failure", "prepare-report-templates.js"),
            workspace.Path,
            eventName,
            inputs,
            missingTemplate ?? "");
        Assert.True(result.ExitCode == 0, result.Output);
        return JsonNode.Parse(result.Output)!;
    }
}
