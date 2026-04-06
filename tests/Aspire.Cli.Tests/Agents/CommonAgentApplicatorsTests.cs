// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    [Fact]
    public void SkillLocation_All_ContainsAllLocations()
    {
        Assert.Equal(4, SkillLocation.All.Count);
        Assert.Contains(SkillLocation.All, l => l == SkillLocation.Standard);
        Assert.Contains(SkillLocation.All, l => l == SkillLocation.ClaudeCode);
        Assert.Contains(SkillLocation.All, l => l == SkillLocation.GitHubSkills);
        Assert.Contains(SkillLocation.All, l => l == SkillLocation.OpenCode);
    }

    [Fact]
    public void SkillLocation_Standard_IsDefaultAndIncludesUserLevel()
    {
        Assert.True(SkillLocation.Standard.IsDefault);
        Assert.True(SkillLocation.Standard.IncludeUserLevel);
        Assert.Equal(Path.Combine(".agents", "skills"), SkillLocation.Standard.RelativeSkillDirectory);
    }

    [Fact]
    public void SkillLocation_ClaudeCode_IsNotDefaultAndNoUserLevel()
    {
        Assert.False(SkillLocation.ClaudeCode.IsDefault);
        Assert.False(SkillLocation.ClaudeCode.IncludeUserLevel);
        Assert.Equal(Path.Combine(".claude", "skills"), SkillLocation.ClaudeCode.RelativeSkillDirectory);
    }

    [Fact]
    public void SkillLocation_OnlyStandardIsDefault()
    {
        Assert.True(SkillLocation.Standard.IsDefault);
        Assert.False(SkillLocation.ClaudeCode.IsDefault);
        Assert.False(SkillLocation.GitHubSkills.IsDefault);
        Assert.False(SkillLocation.OpenCode.IsDefault);
    }

    [Fact]
    public void SkillDefinition_All_ContainsExpectedSkills()
    {
        Assert.Equal(3, SkillDefinition.All.Count);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.Aspire);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.PlaywrightCli);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.DotnetInspect);
    }

    [Fact]
    public void SkillDefinition_OnlyAspireIsDefault()
    {
        Assert.True(SkillDefinition.Aspire.IsDefault);
        Assert.False(SkillDefinition.PlaywrightCli.IsDefault);
        Assert.False(SkillDefinition.DotnetInspect.IsDefault);
    }

    [Fact]
    public void SkillDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], SkillDefinition.DotnetInspect.ApplicableLanguages);
        Assert.Empty(SkillDefinition.Aspire.ApplicableLanguages);
        Assert.Empty(SkillDefinition.PlaywrightCli.ApplicableLanguages);
    }

    [Fact]
    public void SkillDefinition_IsApplicableToLanguage_EmptyApplicableLanguages_AlwaysTrue()
    {
        Assert.True(SkillDefinition.Aspire.IsApplicableToLanguage(null));
        Assert.True(SkillDefinition.Aspire.IsApplicableToLanguage(new LanguageId(KnownLanguageId.CSharp)));
        Assert.True(SkillDefinition.Aspire.IsApplicableToLanguage(new LanguageId(KnownLanguageId.TypeScript)));
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
        Assert.Null(SkillDefinition.PlaywrightCli.EmbeddedResourceRoot);
    }

    [Fact]
    public async Task SkillDefinition_Aspire_HasEmbeddedSkillAssets()
    {
        Assert.Null(SkillDefinition.Aspire.SkillContent);
        Assert.Equal(CommonAgentApplicators.AspireSkillResourceRoot, SkillDefinition.Aspire.EmbeddedResourceRoot);

        var skillFiles = await EmbeddedSkillResourceLoader.LoadTextFilesAsync(SkillDefinition.Aspire.EmbeddedResourceRoot!, CancellationToken.None);

        Assert.Contains(skillFiles, file => file.RelativePath == "SKILL.md");
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("evals", "evals.json"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "agent-workflows.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "app-commands.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "resource-management.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "monitoring.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "deployment.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "tools-and-configuration.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "typescript-apphosts.md"));
        Assert.Contains(skillFiles, file => file.RelativePath == Path.Combine("references", "playwright-handoff.md"));
    }

    [Fact]
    public void SkillDefinition_Aspire_ExcludesEvalsFromInstall()
    {
        Assert.Contains(SkillDefinition.Aspire.InstallExcludedRelativePaths, path => path == Path.Combine("evals"));
        Assert.False(SkillDefinition.Aspire.ShouldInstallFile(Path.Combine("evals", "evals.json")));
        Assert.True(SkillDefinition.Aspire.ShouldInstallFile("SKILL.md"));
    }

    [Fact]
    public void SkillDefinition_DotnetInspect_HasSkillContent()
    {
        Assert.NotNull(SkillDefinition.DotnetInspect.SkillContent);
        Assert.Null(SkillDefinition.DotnetInspect.EmbeddedResourceRoot);
        Assert.Contains("# dotnet-inspect", SkillDefinition.DotnetInspect.SkillContent);
    }
}
