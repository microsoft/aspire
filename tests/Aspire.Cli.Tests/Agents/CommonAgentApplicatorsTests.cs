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
        Assert.Equal(6, AgentAssetLocation.All.Count);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.Standard);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.ClaudeCode);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.GitHubSkills);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.OpenCode);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.GitHubExtensions);
        Assert.Contains(AgentAssetLocation.All, l => l == AgentAssetLocation.CopilotExtensions);
    }

    [Fact]
    public void AgentAssetLocation_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User, AgentAssetLocation.Standard.Scopes);
        Assert.Equal(Path.Combine(".agents", "skills"), AgentAssetLocation.Standard.RelativeDirectory);
    }

    [Fact]
    public void AgentAssetLocation_ClaudeCode_IsNotDefaultAndNoUserLevel()
    {
        Assert.False(AgentAssetLocation.ClaudeCode.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.ClaudeCode.Scopes);
        Assert.Equal(Path.Combine(".claude", "skills"), AgentAssetLocation.ClaudeCode.RelativeDirectory);
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
    public void AgentAssetLocation_Extensions_HaveSeparateWorkspaceAndUserScopes()
    {
        Assert.True(AgentAssetLocation.GitHubExtensions.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.GitHubExtensions.Scopes);
        Assert.Equal(Path.Combine(".github", "extensions"), AgentAssetLocation.GitHubExtensions.RelativeDirectory);

        Assert.False(AgentAssetLocation.CopilotExtensions.IsDefault);
        Assert.Equal(AgentAssetLocationScope.User, AgentAssetLocation.CopilotExtensions.Scopes);
        Assert.Equal(Path.Combine(".copilot", "extensions"), AgentAssetLocation.CopilotExtensions.RelativeDirectory);
    }

    [Fact]
    public void AgentClient_OnlyCopilotCliSupportsExtensions()
    {
        Assert.Contains(AgentAssetKind.Extension, AgentClient.CopilotCli.SupportedAssetKinds);
        Assert.DoesNotContain(AgentAssetKind.Extension, AgentClient.ClaudeCode.SupportedAssetKinds);
        Assert.DoesNotContain(AgentAssetKind.Extension, AgentClient.VsCode.SupportedAssetKinds);
        Assert.DoesNotContain(AgentAssetKind.Extension, AgentClient.OpenCode.SupportedAssetKinds);
    }

    [Fact]
    public void AgentAssetDefinition_CliDefined_ContainsExpectedAgentAssets()
    {
        Assert.Equal(2, AgentAssetDefinition.CliDefined.Count);
        Assert.Contains(AgentAssetDefinition.CliDefined, s => s == AgentAssetDefinition.PlaywrightCli);
        Assert.Contains(AgentAssetDefinition.CliDefined, s => s == AgentAssetDefinition.DotnetInspect);
    }

    [Fact]
    public void AgentAssetDefinition_CliDefinedSkills_AreNotDefault()
    {
        Assert.All(AgentAssetDefinition.CliDefined, static skill => Assert.False(skill.IsDefault));
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
        var bundleSkill = AgentAssetDefinition.CreateAspireSkillsBundle(
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state",
            AgentAssetKind.Skill);

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
    public void AgentAssetDefinition_PlaywrightCli_HasNoAssetContent()
    {
        Assert.Null(AgentAssetDefinition.PlaywrightCli.AssetContent);
        Assert.Equal(AgentAssetSourceKind.ExternalInstaller, AgentAssetDefinition.PlaywrightCli.SourceKind);
        Assert.False(AgentAssetDefinition.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkills_AreExternallySourced()
    {
        Assert.All(
            [
                AgentAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps", AgentAssetKind.Skill),
                AgentAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireifyName, "One-time setup: wire up AppHost with discovered projects", AgentAssetKind.Skill),
                AgentAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireDeploymentSkillName, "Aspire deployment target selection, preflight, publish, and deploy workflows", AgentAssetKind.Skill)
            ],
            skill =>
            {
                Assert.Null(skill.AssetContent);
                Assert.Equal(AgentAssetSourceKind.AspireSkillsBundle, skill.SourceKind);
                Assert.True(skill.HasInstallableFiles);
            });
    }

    [Fact]
    public async Task AgentAssetDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = AgentAssetDefinition.CliDefined
            .Where(static skill => skill.AssetContent is not null);

        foreach (var skill in installableSkills)
        {
            var skillFiles = await GetInstallableAgentAssetFilesAsync(skill);
            var skillFile = Assert.Single(skillFiles, static file => file.RelativePath == "SKILL.md");
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
        var bundleSkill = AgentAssetDefinition.CreateAspireSkillsBundle(
            CommonAgentApplicators.AspireSkillName,
            "Aspire CLI commands and workflows for distributed apps",
            AgentAssetKind.Skill,
            installExcludedRelativePaths: [Path.Combine("evals")]);

        Assert.Contains(bundleSkill.InstallExcludedRelativePaths, path => path == Path.Combine("evals"));
        Assert.False(bundleSkill.ShouldInstallFile(Path.Combine("evals", "evals.json")));
        Assert.True(bundleSkill.ShouldInstallFile("SKILL.md"));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_HasAssetContent()
    {
        Assert.NotNull(AgentAssetDefinition.DotnetInspect.AssetContent);
        Assert.Equal(AgentAssetSourceKind.Static, AgentAssetDefinition.DotnetInspect.SourceKind);
        Assert.True(AgentAssetDefinition.DotnetInspect.HasInstallableFiles);
        Assert.Contains("# dotnet-inspect", AgentAssetDefinition.DotnetInspect.AssetContent);
    }

    private static async Task<IReadOnlyList<AgentAssetFile>> GetInstallableAgentAssetFilesAsync(AgentAssetDefinition asset)
    {
        if (asset.AssetContent is not null && asset.AssetKind == AgentAssetKind.Skill)
        {
            return [new AgentAssetFile("SKILL.md", asset.AssetContent)];
        }

        throw new InvalidOperationException($"Asset '{asset.Name}' does not define installable files.");
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
