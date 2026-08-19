// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    private const int MaxSkillDescriptionLength = 1024;

    [Fact]
    public void AgentAssetLocation_All_ContainsAllLocations()
    {
        Assert.Equal(4, AgentAssetLocation.All.Count);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.Standard);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.ClaudeCode);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.GitHubSkills);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.OpenCode);
    }

    [Fact]
    public void AgentAssetLocation_Standard_IsDefaultForWorkspaceAndUser()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.Equal(
            AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User,
            AgentAssetLocation.Standard.Scopes);
        Assert.Equal(Path.Combine(".agents", "skills"), AgentAssetLocation.Standard.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_ClaudeCode_IsNotDefaultAndWorkspaceOnly()
    {
        Assert.False(AgentAssetLocation.ClaudeCode.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.ClaudeCode.Scopes);
        Assert.Equal(Path.Combine(".claude", "skills"), AgentAssetLocation.ClaudeCode.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_OnlyStandardIsDefault()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.False(AgentAssetLocation.ClaudeCode.IsDefault);
        Assert.False(AgentAssetLocation.GitHubSkills.IsDefault);
        Assert.False(AgentAssetLocation.OpenCode.IsDefault);
    }

    [Fact]
    public void AgentAssetDefinition_CliDefined_ContainsExpectedSkills()
    {
        Assert.Equal(2, AgentAssetDefinition.CliDefined.Count);
        Assert.Contains(AgentAssetDefinition.CliDefined, static asset => asset == AgentAssetDefinition.PlaywrightCli);
        Assert.Contains(AgentAssetDefinition.CliDefined, static asset => asset == AgentAssetDefinition.DotnetInspect);
        Assert.All(AgentAssetDefinition.CliDefined, static asset => Assert.Equal(AgentAssetKind.Skills, asset.AssetKind));
    }

    [Fact]
    public void AgentAssetDefinition_CliDefinedAssets_AreNotDefault()
    {
        Assert.All(AgentAssetDefinition.CliDefined, static asset => Assert.False(asset.IsDefault));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], AgentAssetDefinition.DotnetInspect.ApplicableLanguages);
        Assert.Empty(AgentAssetDefinition.PlaywrightCli.ApplicableLanguages);
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        var bundleSkill = AgentAssetDefinition.CreateAspireSkillsBundleAsset(
            AgentAssetKind.Skills,
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state");

        Assert.True(bundleSkill.IsApplicableToLanguage(null));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_WithRestrictions_MatchesCorrectly()
    {
        // DotnetInspect is restricted to CSharp
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(null)); // no language detected => excluded
        Assert.True(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void AgentAssetDefinition_PlaywrightCli_UsesExternalInstaller()
    {
        Assert.Empty(AgentAssetDefinition.PlaywrightCli.Files);
        Assert.Equal(AgentAssetSourceKind.ExternalInstaller, AgentAssetDefinition.PlaywrightCli.SourceKind);
        Assert.False(AgentAssetDefinition.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void AgentAssetDefinition_BundleAssets_AreExternallySourced()
    {
        Assert.All(
            [
                AgentAssetDefinition.CreateAspireSkillsBundleAsset(AgentAssetKind.Skills, CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps"),
                AgentAssetDefinition.CreateAspireSkillsBundleAsset(AgentAssetKind.Skills, CommonAgentApplicators.AspireifySkillName, "One-time setup: wire up AppHost with discovered projects"),
                AgentAssetDefinition.CreateAspireSkillsBundleAsset(AgentAssetKind.Skills, CommonAgentApplicators.AspireDeploymentSkillName, "Aspire deployment target selection, preflight, publish, and deploy workflows")
            ],
            asset =>
            {
                Assert.Empty(asset.Files);
                Assert.Equal(AgentAssetSourceKind.AspireSkillsBundle, asset.SourceKind);
                Assert.True(asset.HasInstallableFiles);
            });
    }

    [Fact]
    public void AgentAssetDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = AgentAssetDefinition.GetCliDefined(AgentAssetKind.Skills)
            .Where(static asset => asset.Files.Count > 0);

        foreach (var skill in installableSkills)
        {
            var skillFile = Assert.Single(skill.Files, static file => file.RelativePath == "SKILL.md");
            var description = GetFrontmatterValue(skillFile.Content, "description");

            Assert.NotNull(description);
            Assert.False(string.IsNullOrWhiteSpace(description), $"Skill '{skill.Name}' should define a frontmatter description.");
            Assert.True(
                description.Length <= MaxSkillDescriptionLength,
                $"Skill '{skill.Name}' description is {description.Length} characters; agent hosts such as Codex and Copilot CLI accept at most {MaxSkillDescriptionLength}.");
        }
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkill_ExcludesManifestPathsFromInstall()
    {
        var bundleSkill = AgentAssetDefinition.CreateAspireSkillsBundleAsset(
            AgentAssetKind.Skills,
            CommonAgentApplicators.AspireSkillName,
            "Aspire CLI commands and workflows for distributed apps",
            installExcludedRelativePaths: [Path.Combine("evals")]);

        Assert.Contains(bundleSkill.InstallExcludedRelativePaths, path => path == Path.Combine("evals"));
        Assert.False(bundleSkill.ShouldInstallFile(Path.Combine("evals", "evals.json")));
        Assert.True(bundleSkill.ShouldInstallFile("SKILL.md"));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_HasStaticSkillFile()
    {
        Assert.Equal(AgentAssetSourceKind.Static, AgentAssetDefinition.DotnetInspect.SourceKind);
        Assert.True(AgentAssetDefinition.DotnetInspect.HasInstallableFiles);
        var skillFile = Assert.Single(AgentAssetDefinition.DotnetInspect.Files);
        Assert.Equal("SKILL.md", skillFile.RelativePath);
        Assert.Contains("# dotnet-inspect", skillFile.Content);
    }

    private static string? GetFrontmatterValue(string content, string key)
    {
        var normalizedContent = content.ReplaceLineEndings("\n");
        if (!normalizedContent.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var frontmatterEndIndex = normalizedContent.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (frontmatterEndIndex < 0)
        {
            return null;
        }

        // Skill files use YAML frontmatter:
        //   ---
        //   name: aspire
        //   description: "Use when..."
        //   ---
        var frontmatter = normalizedContent[4..frontmatterEndIndex];
        var keyPrefix = $"{key}:";

        foreach (var line in frontmatter.Split('\n'))
        {
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[keyPrefix.Length..].Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
        }

        return null;
    }
}
