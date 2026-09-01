// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    [Fact]
    public void AgentAssetKind_ContainsSkillsAndExtensions()
    {
        Assert.Equal([AgentAssetKind.Skill, AgentAssetKind.Extension], Enum.GetValues<AgentAssetKind>());
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
    public void AgentAssetLocation_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(AgentAssetLocation.Standard.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User, AgentAssetLocation.Standard.Scopes);
        Assert.Equal(Path.Combine(".agents", "skills"), AgentAssetLocation.Standard.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_ClaudeCode_IsNotDefaultAndNoUserLevel()
    {
        Assert.False(AgentAssetLocation.ClaudeCode.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.ClaudeCode.Scopes);
        Assert.Equal(Path.Combine(".claude", "skills"), AgentAssetLocation.ClaudeCode.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_ExtensionLocations_KeepProjectAndUserTargetsSeparate()
    {
        Assert.Equal(
            [AgentAssetLocation.ProjectExtensions, AgentAssetLocation.UserExtensions],
            AgentAssetLocation.GetLocations(AgentAssetKind.Extension));
        Assert.Equal(AgentAssetLocationScope.Workspace, AgentAssetLocation.ProjectExtensions.Scopes);
        Assert.Equal(Path.Combine(".github", "extensions"), AgentAssetLocation.ProjectExtensions.RelativeAssetDirectory);
        Assert.Equal(AgentAssetLocationScope.User, AgentAssetLocation.UserExtensions.Scopes);
        Assert.Equal(Path.Combine(".copilot", "extensions"), AgentAssetLocation.UserExtensions.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetLocation_DefaultsAreScopedByAssetKind()
    {
        Assert.Equal(
            [AgentAssetLocation.Standard],
            AgentAssetLocation.GetLocations(AgentAssetKind.Skill).Where(static location => location.IsDefault));
        Assert.Equal(
            [AgentAssetLocation.ProjectExtensions],
            AgentAssetLocation.GetLocations(AgentAssetKind.Extension).Where(static location => location.IsDefault));
    }

    [Fact]
    public void AgentAssetDefinition_CliDefined_ContainsExpectedSkills()
    {
        Assert.Equal(
            [AgentAssetDefinition.PlaywrightCli, AgentAssetDefinition.DotnetInspect],
            AgentAssetDefinition.CliDefined);
        Assert.All(AgentAssetDefinition.CliDefined, static skill => Assert.False(skill.IsDefault));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], AgentAssetDefinition.DotnetInspect.ApplicableLanguages);
        Assert.Empty(AgentAssetDefinition.PlaywrightCli.ApplicableLanguages);
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(null));
        Assert.True(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(AgentAssetDefinition.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        var skill = AgentAssetDefinition.CreateAspireSkillsBundle(
            AgentAssetKind.Skill,
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state");

        Assert.True(skill.IsApplicableToLanguage(null));
        Assert.True(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void AgentAssetDefinition_PlaywrightCli_HasNoInstallableFiles()
    {
        Assert.Empty(AgentAssetDefinition.PlaywrightCli.Files);
        Assert.Equal(AgentAssetSourceKind.ExternalInstaller, AgentAssetDefinition.PlaywrightCli.SourceKind);
        Assert.False(AgentAssetDefinition.PlaywrightCli.HasInstallableFiles);
    }

    [Theory]
    [InlineData(false, "aspire")]
    [InlineData(false, "aspireify")]
    [InlineData(false, "aspire-deployment")]
    [InlineData(true, "aspire-doctor")]
    public void AgentAssetDefinition_BundleAssets_AreDefaultAndExternallySourced(bool isExtension, string name)
    {
        var kind = isExtension ? AgentAssetKind.Extension : AgentAssetKind.Skill;
        var asset = AgentAssetDefinition.CreateAspireSkillsBundle(kind, name, "An Aspire asset");

        Assert.Equal(kind, asset.AssetKind);
        Assert.Empty(asset.Files);
        Assert.Equal(AgentAssetSourceKind.AspireSkillsBundle, asset.SourceKind);
        Assert.True(asset.HasInstallableFiles);
        Assert.True(asset.IsDefault);
    }

    [Fact]
    public void AgentAssetDefinition_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        foreach (var skill in AgentAssetDefinition.CliDefined.Where(static skill => skill.Files.Count > 0))
        {
            var skillFile = Assert.Single(skill.Files, static file => file.RelativePath == "SKILL.md");
            SkillBundleProvider.ValidateSkillFileFrontmatter(skill.Name, skillFile.Content);
        }
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkill_ExcludesManifestPathsFromInstall()
    {
        var skill = AgentAssetDefinition.CreateAspireSkillsBundle(
            AgentAssetKind.Skill,
            CommonAgentApplicators.AspireSkillName,
            "Aspire CLI commands and workflows for distributed apps",
            installExcludedRelativePaths: ["evals"]);

        Assert.Equal(["evals"], skill.InstallExcludedRelativePaths);
        Assert.False(skill.ShouldInstallFile("evals"));
        Assert.False(skill.ShouldInstallFile(Path.Combine("evals", "evals.json")));
        Assert.True(skill.ShouldInstallFile(Path.Combine("evals-extra", "evals.json")));
        Assert.True(skill.ShouldInstallFile("SKILL.md"));
    }

    [Fact]
    public void AgentAssetDefinition_DotnetInspect_HasSkillContent()
    {
        var skillFile = Assert.Single(AgentAssetDefinition.DotnetInspect.Files);

        Assert.Equal(AgentAssetSourceKind.Static, AgentAssetDefinition.DotnetInspect.SourceKind);
        Assert.True(AgentAssetDefinition.DotnetInspect.HasInstallableFiles);
        Assert.Equal("SKILL.md", skillFile.RelativePath);
        Assert.Contains("# dotnet-inspect", skillFile.Content);
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
        byte[] bytes = [0x00, 0x01, 0xFF];
        var file = new AgentAssetFile("extension.bin", bytes, AgentAssetFileComparison.ExactBytes);
        bytes[0] = 0xFF;

        Assert.True(file.ContentEquals([0x00, 0x01, 0xFF]));
        Assert.False(file.ContentEquals([0x00, 0x01, 0xFE]));
    }
}
