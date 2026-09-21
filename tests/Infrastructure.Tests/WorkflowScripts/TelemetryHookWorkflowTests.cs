// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public class TelemetryHookWorkflowTests
{
    [Fact]
    public void UpdateRequiresImmutableSourceAndVersionWithoutSchedule()
    {
        var workflow = ReadWorkflow("update-telemetry-hooks.yml");
        var triggers = Assert.IsType<YamlMappingNode>(workflow["on"]);
        Assert.Equal(["workflow_dispatch"], triggers.Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        var dispatch = Assert.IsType<YamlMappingNode>(triggers["workflow_dispatch"]);
        var inputs = Assert.IsType<YamlMappingNode>(dispatch["inputs"]);
        Assert.Equal(["source_commit", "version"], inputs.Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        foreach (var input in inputs.Children.Values.Cast<YamlMappingNode>())
        {
            Assert.Equal("true", ((YamlScalarNode)input["required"]).Value);
            Assert.Equal("string", ((YamlScalarNode)input["type"]).Value);
        }

        var concurrency = Assert.IsType<YamlMappingNode>(workflow["concurrency"]);
        Assert.Equal("update-telemetry-hooks", ((YamlScalarNode)concurrency["group"]).Value);
        Assert.Equal("false", ((YamlScalarNode)concurrency["cancel-in-progress"]).Value);
    }

    [Fact]
    public void UpdateChecksOutMain()
    {
        var checkout = Assert.Single(UpdateSteps(), step =>
            step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses) &&
            (uses as YamlScalarNode)?.Value?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);
        var inputs = Assert.IsType<YamlMappingNode>(checkout["with"]);
        Assert.Equal("main", ((YamlScalarNode)inputs["ref"]).Value);
    }

    [Fact]
    public void UpdateRunsOnlyInstallerAndScriptTests()
    {
        var commands = UpdateSteps()
            .Select(step => step.Children.TryGetValue(new YamlScalarNode("run"), out var run) ? (run as YamlScalarNode)?.Value : null)
            .OfType<string>();
        var command = Assert.Single(commands, value => value.Contains("dotnet test", StringComparison.Ordinal));

        // Folded YAML emits --filter-class "*.SomeTests" and trait tokens on one line.
        var classes = Regex.Matches(command, "--filter-class\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value);
        var exclusions = Regex.Matches(command, "--filter-not-trait\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value);
        Assert.Equal(["*.TelemetryHookInstallerTests", "*.TelemetryHookScriptTests"], classes);
        Assert.Equal(["quarantined=true", "outerloop=true"], exclusions);
        Assert.Contains("--no-launch-profile", command, StringComparison.Ordinal);
        Assert.Contains(" -- --filter-class ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SynchronizationBranchMatchesProtectedScriptGuard()
    {
        var step = Assert.Single(UpdateSteps(), step =>
            step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses) &&
            (uses as YamlScalarNode)?.Value == "./.github/actions/create-pull-request");
        var inputs = Assert.IsType<YamlMappingNode>(step["with"]);
        Assert.Equal("update-telemetry-hooks", ((YamlScalarNode)inputs["branch"]).Value);
        Assert.Equal("main", ((YamlScalarNode)inputs["base"]).Value);
        Assert.Equal("true", ((YamlScalarNode)inputs["draft"]).Value);

        var guard = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "verify-telemetry-hook-changes.yml"));
        var allowedBranch = Regex.Match(guard, @"\$allowedHeadRef = '([^']+)'", RegexOptions.CultureInvariant);
        Assert.True(allowedBranch.Success);
        Assert.Equal(((YamlScalarNode)inputs["branch"]).Value, allowedBranch.Groups[1].Value);
    }

    [Fact]
    public void UpdateChecksPendingSynchronizationBeforeReplacingItsPullRequest()
    {
        var step = Assert.Single(UpdateSteps(), step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
            (name as YamlScalarNode)?.Value == "Update canonical telemetry hooks");
        var environment = Assert.IsType<YamlMappingNode>(step["env"]);
        Assert.Equal("${{ inputs.source_commit }}", ((YamlScalarNode)environment["SOURCE_COMMIT"]).Value);
        Assert.Equal("${{ inputs.version }}", ((YamlScalarNode)environment["VERSION"]).Value);
        var command = ((YamlScalarNode)step["run"]).Value!;
        Assert.Contains("$pendingRef = 'refs/heads/update-telemetry-hooks'", command, StringComparison.Ordinal);
        Assert.Contains("git ls-remote --heads origin $pendingRef", command, StringComparison.Ordinal);
        Assert.Contains("refs/remotes/origin/update-telemetry-hooks:src/Aspire.Cli/Agents/Hooks/telemetry-hooks.metadata.json", command, StringComparison.Ordinal);
        Assert.Contains("$arguments.BaselineMetadataPath = $baseline", command, StringComparison.Ordinal);
        Assert.Contains("./eng/scripts/update-telemetry-hooks.ps1 @arguments", command, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationCoversEveryProvenanceInputWithReadOnlyPermissions()
    {
        var workflow = ReadWorkflow("verify-telemetry-hooks.yml");
        var triggers = Assert.IsType<YamlMappingNode>(workflow["on"]);
        var pullRequest = Assert.IsType<YamlMappingNode>(triggers["pull_request"]);
        var paths = Assert.IsType<YamlSequenceNode>(pullRequest["paths"]);
        Assert.Equal(
        [
            "src/Aspire.Cli/Agents/Hooks/track-telemetry.sh",
            "src/Aspire.Cli/Agents/Hooks/track-telemetry.ps1",
            "src/Aspire.Cli/Agents/Hooks/telemetry-hooks.metadata.json",
            "eng/scripts/verify-telemetry-hooks.ps1",
            "eng/scripts/update-telemetry-hooks.ps1",
            "eng/scripts/telemetry-hooks.common.ps1",
            ".github/workflows/verify-telemetry-hooks.yml",
            ".github/workflows/update-telemetry-hooks.yml",
            ".github/workflows/verify-telemetry-hook-changes.yml"
        ],
        paths.Children.Cast<YamlScalarNode>().Select(path => path.Value));

        var permissions = Assert.IsType<YamlMappingNode>(workflow["permissions"]);
        var permission = Assert.Single(permissions.Children);
        Assert.Equal("contents", ((YamlScalarNode)permission.Key).Value);
        Assert.Equal("read", ((YamlScalarNode)permission.Value).Value);
    }

    private static IEnumerable<YamlMappingNode> UpdateSteps()
    {
        var workflow = ReadWorkflow("update-telemetry-hooks.yml");
        var jobs = Assert.IsType<YamlMappingNode>(workflow["jobs"]);
        var job = Assert.IsType<YamlMappingNode>(jobs["update-and-pr"]);
        Assert.Equal("${{ github.repository_owner == 'microsoft' }}", ((YamlScalarNode)job["if"]).Value);
        return Assert.IsType<YamlSequenceNode>(job["steps"]).Children.Cast<YamlMappingNode>();
    }

    private static YamlMappingNode ReadWorkflow(string name)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", name));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }
}
