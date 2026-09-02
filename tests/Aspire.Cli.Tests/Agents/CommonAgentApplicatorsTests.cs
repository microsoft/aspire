// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    private const int MaxSkillDescriptionLength = 1024;

    [Fact]
    public void AgentAssetKind_ContainsSkillMcpAndExtension()
    {
        Assert.Equal(
            [AgentAssetKind.Skill, AgentAssetKind.Mcp, AgentAssetKind.Extension],
            Enum.GetValues<AgentAssetKind>());
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
    public void AgentAssetLocation_All_ContainsSkillAndExtensionLocations()
    {
        Assert.Equal(
            [
                AgentAssetLocation.Standard,
                AgentAssetLocation.ClaudeCode,
                AgentAssetLocation.GitHubSkills,
                AgentAssetLocation.OpenCode,
                AgentAssetLocation.ProjectExtensions,
                AgentAssetLocation.UserExtensions,
            ],
            AgentAssetLocation.All);
    }

    [Fact]
    public void AgentAssetLocation_ExtensionLocations_KeepProjectAndUserTargetsSeparate()
    {
        Assert.Equal(
            [AgentAssetLocation.ProjectExtensions, AgentAssetLocation.UserExtensions],
            AgentAssetLocation.GetLocations(AgentAssetKind.Extension));

        Assert.Equal(AgentAssetKind.Extension, AgentAssetLocation.ProjectExtensions.AssetKind);
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.ProjectExtensions.Scopes);
        Assert.Equal(Path.Combine(".github", "extensions"), AgentAssetLocation.ProjectExtensions.RelativeAssetDirectory);

        Assert.Equal(AgentAssetKind.Extension, AgentAssetLocation.UserExtensions.AssetKind);
        Assert.Equal(AgentAssetLocationScope.User, AgentAssetLocation.UserExtensions.Scopes);
        Assert.Equal(Path.Combine(".copilot", "extensions"), AgentAssetLocation.UserExtensions.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_DefaultsAreScopedByAssetKind()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.False(AgentAssetLocation.ClaudeCode.IsDefault);
        Assert.False(AgentAssetLocation.GitHubSkills.IsDefault);
        Assert.False(AgentAssetLocation.OpenCode.IsDefault);
        Assert.True(AgentAssetLocation.ProjectExtensions.IsDefault);
        Assert.False(AgentAssetLocation.UserExtensions.IsDefault);
    }

    [Fact]
    public void AgentAssetLocation_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.Equal(
            AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User,
            AgentAssetLocation.Standard.Scopes);
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
    public void AgentAssetLocation_GetDefaultLocations_WithoutDetectedClients_UsesStaticDefault()
    {
        AssertDefaultLocations([], AgentAssetLocation.Standard);
    }

    [Fact]
    public void AgentAssetLocation_GetDefaultLocations_ClaudeCode_UsesClaudeLocation()
    {
        AssertDefaultLocations([AgentClientKind.ClaudeCode], AgentAssetLocation.ClaudeCode);
    }

    [Fact]
    public void AgentAssetLocation_GetDefaultLocations_VsCode_UsesStandardLocation()
    {
        AssertDefaultLocations([AgentClientKind.VsCode], AgentAssetLocation.Standard);
    }

    [Fact]
    public void AgentAssetLocation_GetDefaultLocations_MixedClients_UsesEachRecommendedLocation()
    {
        AssertDefaultLocations(
            [AgentClientKind.VsCode, AgentClientKind.ClaudeCode],
            AgentAssetLocation.Standard,
            AgentAssetLocation.ClaudeCode);
    }

    [Fact]
    public void AgentAssetLocation_GetDefaultLocations_McpHasNoLocations()
    {
        Assert.Empty(AgentAssetLocation.GetDefaultLocations(
            AgentAssetKind.Mcp,
            [AgentClientKind.VsCode]));
    }

    [Fact]
    public void AgentAssetLocation_GetDefaultLocations_CopilotApp_UsesProjectExtensionLocation()
    {
        Assert.Equal(
            [AgentAssetLocation.ProjectExtensions],
            AgentAssetLocation.GetDefaultLocations(
                AgentAssetKind.Extension,
                [AgentClientKind.CopilotApp]));
    }

    [Fact]
    public void AgentAssetDefinition_CliDefined_ContainsExpectedAssets()
    {
        Assert.Equal(
            [
                AgentAssetCatalog.PlaywrightCli,
                AgentAssetCatalog.DotnetInspect,
                AgentAssetCatalog.AspireMcpServer,
            ],
            AgentAssetCatalog.All);
    }

    [Fact]
    public void AgentAssetDefinition_CliDefinedAssets_AreNotDefault()
    {
        Assert.All(AgentAssetCatalog.All, static asset => Assert.False(asset.IsDefault));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], AgentAssetCatalog.DotnetInspect.ApplicableLanguages);
        Assert.Empty(AgentAssetCatalog.PlaywrightCli.ApplicableLanguages);
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        var bundleSkill = AgentFileAssetDefinition.CreateAspireSkillsBundle(
            AgentAssetKind.Skill,
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state");

        Assert.True(bundleSkill.IsApplicableToLanguage(null));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(bundleSkill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_WithRestrictions_MatchesCorrectly()
    {
        Assert.False(AgentAssetCatalog.DotnetInspect.IsApplicableToLanguage(null));
        Assert.True(AgentAssetCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(AgentAssetCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(AgentAssetCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void AgentAssetDefinition_PlaywrightCli_IsSkillOnlyAndUsesExternalInstaller()
    {
        Assert.Equal(AgentAssetKind.Skill, AgentAssetCatalog.PlaywrightCli.AssetKind);
        Assert.Empty(AgentAssetCatalog.PlaywrightCli.Files);
        Assert.Equal(AgentFileAssetSourceKind.ExternalInstaller, AgentAssetCatalog.PlaywrightCli.SourceKind);
        Assert.False(AgentAssetCatalog.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkills_AreExternallySourced()
    {
        Assert.All(
            [
                AgentFileAssetDefinition.CreateAspireSkillsBundle(AgentAssetKind.Skill, CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps"),
                AgentFileAssetDefinition.CreateAspireSkillsBundle(AgentAssetKind.Skill, CommonAgentApplicators.AspireifySkillName, "One-time setup: wire up AppHost with discovered projects"),
                AgentFileAssetDefinition.CreateAspireSkillsBundle(AgentAssetKind.Skill, CommonAgentApplicators.AspireDeploymentSkillName, "Aspire deployment target selection, preflight, publish, and deploy workflows")
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
    public void AgentAssetDefinition_ExtensionBundleAsset_UsesExtensionKindAndSource()
    {
        var extension = AgentFileAssetDefinition.CreateAspireSkillsBundle(
            AgentAssetKind.Extension,
            "aspire-doctor",
            "Runs Aspire doctor in a canvas");

        Assert.Equal(AgentAssetKind.Extension, extension.AssetKind);
        Assert.Empty(extension.Files);
        Assert.Equal(AgentFileAssetSourceKind.AspireSkillsBundle, extension.SourceKind);
        Assert.True(extension.HasInstallableFiles);
    }

    [Fact]
    public void AgentAssetDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = AgentAssetCatalog.GetFileAssets(AgentAssetKind.Skill)
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
            AgentAssetKind.Skill,
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
        var skillFile = Assert.Single(AgentAssetCatalog.DotnetInspect.Files);

        Assert.Equal(AgentFileAssetSourceKind.Static, AgentAssetCatalog.DotnetInspect.SourceKind);
        Assert.True(AgentAssetCatalog.DotnetInspect.HasInstallableFiles);
        Assert.Equal("SKILL.md", skillFile.RelativePath);
        Assert.Contains("# dotnet-inspect", skillFile.Content);
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

        // Extensions are a Copilot App concept today; every other client must not silently
        // receive an extension tree it cannot load.
        Assert.True(AgentClientKind.CopilotApp.Supports(AgentAssetKind.Extension));
        Assert.All(
            clients.Where(static client => client is not AgentClientKind.CopilotApp),
            static client => Assert.False(client.Supports(AgentAssetKind.Extension)));
    }

    [Fact]
    public void EveryAgentAssetKind_HasSupportingClient()
    {
        var clients = Enum.GetValues<AgentClientKind>();

        Assert.All(
            Enum.GetValues<AgentAssetKind>(),
            assetKind => Assert.Contains(clients, client => client.Supports(assetKind)));
    }

    [Fact]
    public void AgentAssetDefinition_AspireMcpServer_IsActionBackedAndNonDefault()
    {
        var mcpAsset = Assert.Single(AgentAssetCatalog.GetActionAssets(AgentAssetKind.Mcp));

        Assert.Same(AgentAssetCatalog.AspireMcpServer, mcpAsset);
        Assert.False(mcpAsset.IsDefault);
        Assert.All(
            AgentAssetCatalog.All.Where(static asset => asset.AssetKind is AgentAssetKind.Skill),
            static skill => Assert.IsType<AgentFileAssetDefinition>(skill));
        Assert.All(
            AgentAssetCatalog.All.Where(static asset => asset.AssetKind is AgentAssetKind.Mcp),
            static mcp => Assert.IsType<AgentActionAssetDefinition>(mcp));
    }

    [Fact]
    public void AgentFileAssetDefinition_RejectsMcpKind()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentFileAssetDefinition(
            AgentAssetKind.Mcp,
            "invalid",
            "Invalid file-backed MCP asset",
            AgentFileAssetSourceKind.Static,
            files: [],
            installExcludedRelativePaths: [],
            isDefault: false));

        Assert.Equal("assetKind", exception.ParamName);
    }

    [Fact]
    public void AgentActionAssetDefinition_RejectsSkillKind()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentActionAssetDefinition(
            AgentAssetKind.Skill,
            "invalid",
            "Invalid action-backed Skill asset",
            isDefault: false));

        Assert.Equal("assetKind", exception.ParamName);
    }

    [Fact]
    public void AgentAssetKind_GetBackingKind_ReturnsExpectedBacking()
    {
        Assert.Equal(AgentAssetBackingKind.File, AgentAssetKind.Skill.GetBackingKind());
        Assert.Equal(AgentAssetBackingKind.Action, AgentAssetKind.Mcp.GetBackingKind());
        Assert.Equal(AgentAssetBackingKind.File, AgentAssetKind.Extension.GetBackingKind());
    }

    [Fact]
    public void AgentAssetFile_NormalizedTextComparison_IgnoresBomAndLineEndings()
    {
        var file = new AgentAssetFile("SKILL.md", "first\nsecond\n");
        var existingContent = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("first\r\nsecond\r\n"))
            .ToArray();

        Assert.True(file.ContentEquals(existingContent));
    }

    [Fact]
    public void AgentAssetFile_NormalizedTextComparison_RejectsInvalidUtf8()
    {
        var file = new AgentAssetFile("SKILL.md", "valid");

        Assert.False(file.ContentEquals([0xFF]));
    }

    [Fact]
    public void AgentAssetFile_ExactByteComparison_RequiresIdenticalBytes()
    {
        var file = new AgentAssetFile(
            "extension.bin",
            [0x00, 0x01, 0xFF],
            AgentAssetFileComparison.ExactBytes);

        Assert.True(file.ContentEquals([0x00, 0x01, 0xFF]));
        Assert.False(file.ContentEquals([0x00, 0x01, 0xFE]));
    }

    private static void AssertDefaultLocations(
        IReadOnlyCollection<AgentClientKind> detectedClients,
        params AgentAssetLocation[] expectedLocations)
    {
        Assert.Equal(
            expectedLocations,
            AgentAssetLocation.GetDefaultLocations(AgentAssetKind.Skill, detectedClients));
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
