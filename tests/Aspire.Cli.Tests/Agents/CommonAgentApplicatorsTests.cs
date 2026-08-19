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
    public void SkillDefinition_CliDefined_ContainsExpectedSkills()
    {
        Assert.Equal(2, SkillDefinition.CliDefined.Count);
        Assert.Contains(SkillDefinition.CliDefined, s => s == SkillDefinition.PlaywrightCli);
        Assert.Contains(SkillDefinition.CliDefined, s => s == SkillDefinition.DotnetInspect);
    }

    [Fact]
    public void SkillDefinition_CliDefinedSkills_AreNotDefault()
    {
        Assert.All(SkillDefinition.CliDefined, static skill => Assert.False(skill.IsDefault));
    }

    [Fact]
    public void SkillDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], SkillDefinition.DotnetInspect.ApplicableLanguages);
        Assert.Empty(SkillDefinition.PlaywrightCli.ApplicableLanguages);
    }

    [Fact]
    public void SkillDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        var bundleSkill = SkillDefinition.CreateAspireSkillsBundle(
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state");

        Assert.True(bundleSkill.IsApplicableToLanguage(null));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void SkillDefinition_IsApplicableToLanguage_WithRestrictions_MatchesCorrectly()
    {
        // DotnetInspect is restricted to CSharp
        Assert.False(SkillDefinition.DotnetInspect.IsApplicableToLanguage(null)); // no language detected => excluded
        Assert.True(SkillDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(SkillDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(SkillDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void SkillDefinition_PlaywrightCli_HasNoSkillContent()
    {
        Assert.Null(SkillDefinition.PlaywrightCli.SkillContent);
        Assert.Equal(SkillSourceKind.ExternalInstaller, SkillDefinition.PlaywrightCli.SourceKind);
        Assert.False(SkillDefinition.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void SkillDefinition_BundleSkills_AreExternallySourced()
    {
        Assert.All(
            [
                SkillDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps"),
                SkillDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireifySkillName, "One-time setup: wire up AppHost with discovered projects"),
                SkillDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireDeploymentSkillName, "Aspire deployment target selection, preflight, publish, and deploy workflows")
            ],
            skill =>
            {
                Assert.Null(skill.SkillContent);
                Assert.Equal(SkillSourceKind.AspireSkillsBundle, skill.SourceKind);
                Assert.True(skill.HasInstallableFiles);
            });
    }

    [Fact]
    public async Task SkillDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = SkillDefinition.CliDefined
            .Where(static skill => skill.SkillContent is not null);

        foreach (var skill in installableSkills)
        {
            var skillFiles = await GetInstallableSkillFilesAsync(skill);
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
    public void SkillDefinition_BundleSkill_ExcludesManifestPathsFromInstall()
    {
        var bundleSkill = SkillDefinition.CreateAspireSkillsBundle(
            CommonAgentApplicators.AspireSkillName,
            "Aspire CLI commands and workflows for distributed apps",
            installExcludedRelativePaths: [Path.Combine("evals")]);

        Assert.Contains(bundleSkill.InstallExcludedRelativePaths, path => path == Path.Combine("evals"));
        Assert.False(bundleSkill.ShouldInstallFile(Path.Combine("evals", "evals.json")));
        Assert.True(bundleSkill.ShouldInstallFile("SKILL.md"));
    }

    [Fact]
    public void SkillDefinition_DotnetInspect_HasSkillContent()
    {
        Assert.NotNull(SkillDefinition.DotnetInspect.SkillContent);
        Assert.Equal(SkillSourceKind.Static, SkillDefinition.DotnetInspect.SourceKind);
        Assert.True(SkillDefinition.DotnetInspect.HasInstallableFiles);
        Assert.Contains("# dotnet-inspect", SkillDefinition.DotnetInspect.SkillContent);
    }

    private static async Task<IReadOnlyList<SkillAssetFile>> GetInstallableSkillFilesAsync(SkillDefinition skill)
    {
        if (skill.SkillContent is not null)
        {
            return [new SkillAssetFile("SKILL.md", skill.SkillContent)];
        }

        throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
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
