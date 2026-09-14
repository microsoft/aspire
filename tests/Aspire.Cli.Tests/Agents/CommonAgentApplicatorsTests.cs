// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    [Fact]
    public void Catalogs_DeclareTheirSupportedClients()
    {
        var source = new FakeAgentAssetSource();
        Assert.Equal(AgentClient.All, new SkillCatalog(source).SupportedClients);
        Assert.Equal([AgentClient.CopilotApp], new ExtensionCatalog(source).SupportedClients);
    }

    [Fact]
    public void AgentAssetCatalogs_DeclareSkillAndExtensionLocations()
    {
        Assert.Equal(
            [
                SkillCatalog.Standard,
                SkillCatalog.ClaudeCode,
                SkillCatalog.GitHubSkills,
                SkillCatalog.OpenCode,
                ExtensionCatalog.ProjectExtensions,
                ExtensionCatalog.UserExtensions,
            ],
            SkillCatalog.KnownLocations.Concat(ExtensionCatalog.KnownLocations));
    }

    [Fact]
    public void SkillCatalog_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(SkillCatalog.Standard.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User, SkillCatalog.Standard.Scopes);
        Assert.Equal(Path.Combine(".agents", "skills"), SkillCatalog.Standard.RelativeAssetDirectory);
    }

    [Fact]
    public void SkillCatalog_ClaudeCode_IsNotDefaultAndNoUserLevel()
    {
        Assert.False(SkillCatalog.ClaudeCode.IsDefault);
        Assert.Equal(AgentAssetLocationScope.Workspace, SkillCatalog.ClaudeCode.Scopes);
        Assert.Equal(Path.Combine(".claude", "skills"), SkillCatalog.ClaudeCode.RelativeAssetDirectory);
    }

    [Fact]
    public void ExtensionCatalog_Locations_KeepProjectAndUserTargetsSeparate()
    {
        Assert.Equal(
            [ExtensionCatalog.ProjectExtensions, ExtensionCatalog.UserExtensions],
            ExtensionCatalog.KnownLocations);
        Assert.Equal(AgentAssetLocationScope.Workspace, ExtensionCatalog.ProjectExtensions.Scopes);
        Assert.Equal(Path.Combine(".github", "extensions"), ExtensionCatalog.ProjectExtensions.RelativeAssetDirectory);
        Assert.Equal(AgentAssetLocationScope.User, ExtensionCatalog.UserExtensions.Scopes);
        Assert.Equal(Path.Combine(".copilot", "extensions"), ExtensionCatalog.UserExtensions.RelativeAssetDirectory);
    }

    [Fact]
    public void AgentAssetCatalogs_DefaultLocationsAreIndependent()
    {
        Assert.Equal(
            [SkillCatalog.Standard],
            SkillCatalog.KnownLocations.Where(static location => location.IsDefault));
        Assert.Equal(
            [ExtensionCatalog.ProjectExtensions],
            ExtensionCatalog.KnownLocations.Where(static location => location.IsDefault));
    }

    [Fact]
    public void SkillCatalog_CliDefined_ContainsExpectedSkills()
    {
        Assert.Equal(
            [SkillCatalog.PlaywrightCli, SkillCatalog.DotnetInspect],
            SkillCatalog.CliDefined);
        Assert.All(SkillCatalog.CliDefined, static skill => Assert.False(skill.IsDefault));
    }

    [Fact]
    public void SkillCatalog_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], SkillCatalog.DotnetInspect.ApplicableLanguages);
        Assert.Empty(SkillCatalog.PlaywrightCli.ApplicableLanguages);
        Assert.False(SkillCatalog.DotnetInspect.IsApplicableToLanguage(null));
        Assert.True(SkillCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.False(SkillCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
        Assert.False(SkillCatalog.DotnetInspect.IsApplicableToLanguage(new LanguageId(KnownLanguageId.Python)));
    }

    [Fact]
    public void AgentAssetDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        var skill = new AgentAssetDefinition(
            "aspire-monitoring",
            "Observe Aspire apps with logs, traces, metrics, and resource state",
            [new AgentAssetFile("SKILL.md", "Skill content")],
            installExcludedRelativePaths: [],
            isDefault: true);

        Assert.True(skill.IsApplicableToLanguage(null));
        Assert.True(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(skill.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
    }

    [Fact]
    public void SkillCatalog_PlaywrightCli_HasNoInstallableFiles()
    {
        Assert.Empty(SkillCatalog.PlaywrightCli.Files);
        Assert.False(SkillCatalog.PlaywrightCli.HasInstallableFiles);
    }

    [Fact]
    public void SkillCatalog_StaticInstallableSkillDescriptionsFitAgentHostLimits()
    {
        foreach (var skill in SkillCatalog.CliDefined.Where(static skill => skill.Files.Count > 0))
        {
            var skillFile = Assert.Single(skill.Files, static file => file.RelativePath == "SKILL.md");
            SkillFileValidator.Validate(skill.Name, skillFile.Bytes.Span);
        }
    }

    [Fact]
    public void AgentAssetDefinition_BundleSkill_ExcludesManifestPathsFromInstall()
    {
        var skill = new AgentAssetDefinition(
            CommonAgentApplicators.AspireSkillName,
            "Aspire CLI commands and workflows for distributed apps",
            [
                new AgentAssetFile(Path.Combine("evals-extra", "evals.json"), "{}"),
                new AgentAssetFile("evals", "Excluded"),
                new AgentAssetFile(Path.Combine("evals", "evals.json"), "{}"),
                new AgentAssetFile("SKILL.md", "Skill content")
            ],
            installExcludedRelativePaths: ["evals"],
            isDefault: true);

        Assert.Equal(
            ["SKILL.md", Path.Combine("evals-extra", "evals.json")],
            skill.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void SkillCatalog_DotnetInspect_HasSkillContent()
    {
        var skillFile = Assert.Single(SkillCatalog.DotnetInspect.Files);

        Assert.True(SkillCatalog.DotnetInspect.HasInstallableFiles);
        Assert.Equal("SKILL.md", skillFile.RelativePath);
        Assert.Contains("# dotnet-inspect", skillFile.Content);
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public void AgentAssetFile_NormalizedTextComparison_IgnoresBomAndLineEndings(int codePage)
    {
        var file = new AgentAssetFile("SKILL.md", "first\nsecond\n");
        var encoding = Encoding.GetEncoding(codePage);
        var existingContent = encoding.GetPreamble()
            .Concat(encoding.GetBytes("first\r\nsecond\r\n"))
            .ToArray();

        Assert.True(file.ContentEquals(existingContent));
    }

    [Fact]
    public void AgentAssetFile_NormalizedTextComparison_UsesReplacementDecoding()
    {
        var file = new AgentAssetFile("SKILL.md", "A\uFFFD");

        Assert.True(file.ContentEquals([0x41, 0xff]));
    }

    [Fact]
    public void AgentAssetFile_NormalizedUtf8Comparison_RejectsInvalidUtf8()
    {
        var file = new AgentAssetFile("extension.mjs", Encoding.UTF8.GetBytes("A\uFFFD"), AgentAssetFileComparison.NormalizedUtf8Text);

        Assert.False(file.ContentEquals([0x41, 0xff]));
    }

    [Fact]
    public void AgentAssetFile_NormalizedUtf8Comparison_AcceptsIdenticalNonUtf8Bytes()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Write-Host 'Hello'")).ToArray();
        var file = new AgentAssetFile("script.ps1", bytes, AgentAssetFileComparison.NormalizedUtf8Text);

        Assert.True(file.ContentEquals(bytes));
        Assert.False(file.ContentEquals(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Write-Host 'Changed'")).ToArray()));
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
