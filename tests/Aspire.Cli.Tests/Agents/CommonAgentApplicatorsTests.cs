// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    private const int MaxSkillDescriptionLength = 1024;

    [Fact]
    public void AgentAssetKind_ContainsOnlySkillAndMcp()
    {
        Assert.Equal([AgentAssetKind.Skill, AgentAssetKind.Mcp], Enum.GetValues<AgentAssetKind>());
    }

    [Fact]
    public void AgentAssetLocation_SkillLocations_ContainExpectedFileLocations()
    {
        Assert.Equal(
            [
                AgentAssetLocation.Standard,
                AgentAssetLocation.ClaudeCode,
                AgentAssetLocation.GitHubSkills,
                AgentAssetLocation.OpenCode,
            ],
            AgentAssetLocation.GetLocations(AgentAssetKind.Skill));
    }

    [Fact]
    public void AgentAssetLocation_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.True(AgentAssetLocation.Standard.IncludeUserLevel);
        Assert.Equal(Path.Combine(".agents", "skills"), AgentAssetLocation.Standard.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_McpHasNoFileLocations()
    {
        Assert.Empty(AgentAssetLocation.GetLocations(AgentAssetKind.Mcp));
        Assert.All(
            AgentAssetLocation.All,
            static location => Assert.False(string.IsNullOrWhiteSpace(location.RelativeAssetDirectory)));
    }

    [Fact]
    public void AgentAssetDefinition_CliDefined_ContainsExpectedAssets()
    {
        Assert.Equal(
            [
                AgentAssetDefinition.PlaywrightCli,
                AgentAssetDefinition.DotnetInspect,
                AgentAssetDefinition.AspireMcpServer,
            ],
            AgentAssetDefinition.CliDefined);
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
        var bundleSkill = AgentFileAssetDefinition.CreateAspireSkillsBundle(
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state");

        Assert.True(bundleSkill.IsApplicableToLanguage(null));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_WithRestrictions_MatchesCorrectly()
    {
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(null));
        Assert.True(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void AgentAssetDefinition_PlaywrightCli_IsSkillOnlyAndUsesExternalInstaller()
    {
        Assert.Equal(AgentAssetKind.Skill, AgentAssetDefinition.PlaywrightCli.AssetKind);
        Assert.Empty(AgentAssetDefinition.PlaywrightCli.Files);
        Assert.Equal(AgentFileAssetSourceKind.ExternalInstaller, AgentAssetDefinition.PlaywrightCli.SourceKind);
        Assert.False(AgentAssetDefinition.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkills_AreExternallySourced()
    {
        Assert.All(
            [
                AgentFileAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps"),
                AgentFileAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireifySkillName, "One-time setup: wire up AppHost with discovered projects"),
                AgentFileAssetDefinition.CreateAspireSkillsBundle(CommonAgentApplicators.AspireDeploymentSkillName, "Aspire deployment target selection, preflight, publish, and deploy workflows")
            ],
            skill =>
            {
                Assert.Equal(AgentAssetKind.Skill, skill.AssetKind);
                Assert.Empty(skill.Files);
                Assert.Equal(AgentFileAssetSourceKind.AspireSkillsBundle, skill.SourceKind);
                Assert.True(skill.HasInstallableFiles);
            });
    }

    [Fact]
    public void AgentAssetDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = AgentAssetDefinition.GetCliDefinedFileAssets(AgentAssetKind.Skill)
            .Where(static skill => skill.Files.Count > 0);

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
        var bundleSkill = AgentFileAssetDefinition.CreateAspireSkillsBundle(
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
        var skillFile = Assert.Single(AgentAssetDefinition.DotnetInspect.Files);

        Assert.Equal(AgentFileAssetSourceKind.Static, AgentAssetDefinition.DotnetInspect.SourceKind);
        Assert.True(AgentAssetDefinition.DotnetInspect.HasInstallableFiles);
        Assert.Contains("# dotnet-inspect", skillFile.Content);
    }

    [Fact]
    public void AgentAssetDefinition_AspireMcpServer_IsActionBackedAndNonDefault()
    {
        var mcpAsset = Assert.Single(AgentAssetDefinition.GetCliDefinedActionAssets(AgentAssetKind.Mcp));

        Assert.Same(AgentAssetDefinition.AspireMcpServer, mcpAsset);
        Assert.False(mcpAsset.IsDefault);
        Assert.All(
            AgentAssetDefinition.CliDefined.Where(static asset => asset.AssetKind is AgentAssetKind.Skill),
            static skill => Assert.IsType<AgentFileAssetDefinition>(skill));
        Assert.All(
            AgentAssetDefinition.CliDefined.Where(static asset => asset.AssetKind is AgentAssetKind.Mcp),
            static mcp => Assert.IsType<AgentActionAssetDefinition>(mcp));
    }

    [Fact]
    public void AgentClientKind_AllKnownClientsSupportSkillsAndMcp()
    {
        AgentClientKind[] clients =
        [
            AgentClientKind.CopilotCli,
            AgentClientKind.CopilotApp,
            AgentClientKind.VsCode,
            AgentClientKind.ClaudeCode,
            AgentClientKind.OpenCode,
        ];

        Assert.All(clients, static client =>
        {
            Assert.True(client.Supports(AgentAssetKind.Skill));
            Assert.True(client.Supports(AgentAssetKind.Mcp));
        });
    }

    [Fact]
    public void EveryAgentAssetKind_HasSupportingClient()
    {
        var clients = Enum.GetValues<AgentClientKind>();

        Assert.All(
            Enum.GetValues<AgentAssetKind>(),
            assetKind => Assert.Contains(clients, client => client.Supports(assetKind)));
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
