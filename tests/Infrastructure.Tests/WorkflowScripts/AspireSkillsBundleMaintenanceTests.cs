// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public class AspireSkillsBundleMaintenanceTests
{
    [Fact]
    public void UpdateWorkflowRunsSurvivingIntegrityAndHookCoverage()
    {
        var workflow = ReadWorkflow();
        var jobs = Assert.IsType<YamlMappingNode>(workflow["jobs"]);
        var job = Assert.IsType<YamlMappingNode>(jobs["update-and-pr"]);
        var steps = Assert.IsType<YamlSequenceNode>(job["steps"]);
        var commands = steps.Children.OfType<YamlMappingNode>()
            .Select(step => step.Children.TryGetValue(new YamlScalarNode("run"), out var run) ? (run as YamlScalarNode)?.Value : null)
            .OfType<string>();
        var testCommand = Assert.Single(commands, command => command.Contains("dotnet test", StringComparison.Ordinal));

        // The folded YAML command contains --filter-class "*.SomeTests" and
        // --filter-not-trait "quarantined=true" tokens, not VSTest filter expressions.
        var classes = Regex.Matches(testCommand, "--filter-class\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)
            .Cast<Match>().Select(match => match.Groups[1].Value);
        var exclusions = Regex.Matches(testCommand, "--filter-not-trait\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant)
            .Cast<Match>().Select(match => match.Groups[1].Value);

        Assert.Equal(
            ["*.TelemetryHookArchiveIntegrityTests", "*.AgentInitCommandTests", "*.TelemetryHookInstallerTests", "*.TelemetryHookScriptTests"],
            classes);
        Assert.Equal(["quarantined=true", "outerloop=true"], exclusions);
        Assert.Contains("--no-launch-profile", testCommand, StringComparison.Ordinal);
        Assert.Contains(" -- --filter-class ", testCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateWorkflowKeepsItsScheduleAndRepositoryBranchGuards()
    {
        var workflow = ReadWorkflow();
        var triggers = Assert.IsType<YamlMappingNode>(workflow["on"]);
        Assert.Equal(["schedule", "workflow_dispatch"], triggers.Children.Keys.Select(key => ((YamlScalarNode)key).Value).Order(StringComparer.Ordinal));
        var schedule = Assert.IsType<YamlSequenceNode>(triggers["schedule"]);
        var cron = Assert.IsType<YamlMappingNode>(Assert.Single(schedule.Children));
        Assert.Equal("0 17 * * *", ((YamlScalarNode)cron["cron"]).Value);

        var jobs = Assert.IsType<YamlMappingNode>(workflow["jobs"]);
        var job = Assert.IsType<YamlMappingNode>(jobs["update-and-pr"]);
        Assert.Equal("${{ github.repository_owner == 'microsoft' }}", ((YamlScalarNode)job["if"]).Value);
        var steps = Assert.IsType<YamlSequenceNode>(job["steps"]);
        var prStep = Assert.Single(steps.Children.OfType<YamlMappingNode>(), step =>
            step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses) &&
            (uses as YamlScalarNode)?.Value == "./.github/actions/create-pull-request");
        var inputs = Assert.IsType<YamlMappingNode>(prStep["with"]);
        Assert.Equal("update-aspire-skills-bundle", ((YamlScalarNode)inputs["branch"]).Value);
        Assert.Equal("main", ((YamlScalarNode)inputs["base"]).Value);
        Assert.Equal("true", ((YamlScalarNode)inputs["draft"]).Value);
    }

    [Fact]
    public void UpdateScriptUsesMetadataVersionAndOnlyStampsRetainedInputs()
    {
        var script = ReadUpdateScript();

        // Persisted text updates use: Set-TextFile -Path $metadataPath -Content ...
        // The complete target list prevents reintroducing a runtime source version stamp.
        var targets = Regex.Matches(script, @"(?m)^\s*Set-TextFile -Path \$(\w+) -Content ", RegexOptions.CultureInvariant)
            .Cast<Match>().Select(match => match.Groups[1].Value);
        Assert.Equal(["metadataPath", "cliProjectPath"], targets);
        Assert.Contains("$metadata = Get-Content -Raw -Path $metadataPath | ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.Contains("return Get-UnprefixedVersion $metadata.version", script, StringComparison.Ordinal);
        Assert.Contains(
            """
            $normalizedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
                Get-CurrentEmbeddedVersion
            }
            """.ReplaceLineEndings("\n"),
            script, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateScriptKeepsArchiveAttestationAndCommitPinnedHookMaintenance()
    {
        var script = ReadUpdateScript();
        const string attestation = "Invoke-GitHubCli attestation verify $archivePath --repo $Repository --cert-identity $certIdentity --cert-oidc-issuer 'https://token.actions.githubusercontent.com'";
        const string copy = "Copy-Item -Path $archivePath -Destination $targetArchivePath -Force";
        Assert.Contains(attestation, script, StringComparison.Ordinal);
        Assert.Contains(copy, script, StringComparison.Ordinal);
        Assert.True(script.IndexOf(attestation, StringComparison.Ordinal) < script.IndexOf(copy, StringComparison.Ordinal));
        Assert.Contains(
            """
            $certIdentity = "https://github.com/$Repository/.github/workflows/publish.yml@refs/tags/$($release.tagName)"
            """,
            script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA512 $archivePath", script, StringComparison.Ordinal);
        Assert.Contains("Get-AspireSkillsReleaseCommitSha -Repository $Repository -Tag $release.tagName", script, StringComparison.Ordinal);
        Assert.Contains("Get-AspireSkillsHookContent -Repository $Repository -CommitSha $hookCommitSha -FileName $hookFileName", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllBytes((Join-Path $hooksDir $hookFileName), $hookContents[$hookFileName])", script, StringComparison.Ordinal);
    }

    private static YamlMappingNode ReadWorkflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "update-aspire-skills-bundle.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static string ReadUpdateScript() =>
        File.ReadAllText(Path.Combine(RepoRoot.Path, "eng", "scripts", "update-aspire-skills-bundle.ps1")).ReplaceLineEndings("\n");
}
