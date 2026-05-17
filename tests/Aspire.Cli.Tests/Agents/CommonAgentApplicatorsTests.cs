// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    private const int MaxSkillDescriptionLength = 1024;

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
        Assert.Equal(4, SkillDefinition.All.Count);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.Aspire);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.Aspireify);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.PlaywrightCli);
        Assert.Contains(SkillDefinition.All, s => s == SkillDefinition.DotnetInspect);
    }

    [Fact]
    public void SkillDefinition_DefaultSkills()
    {
        Assert.True(SkillDefinition.Aspire.IsDefault);
        Assert.True(SkillDefinition.Aspireify.IsDefault);
        Assert.False(SkillDefinition.PlaywrightCli.IsDefault);
        Assert.False(SkillDefinition.DotnetInspect.IsDefault);
    }

    [Fact]
    public void SkillDefinition_DotnetInspect_IsRestrictedToCSharp()
    {
        Assert.Equal([KnownLanguageId.CSharp], SkillDefinition.DotnetInspect.ApplicableLanguages);
        Assert.Empty(SkillDefinition.Aspire.ApplicableLanguages);
        Assert.Empty(SkillDefinition.Aspireify.ApplicableLanguages);
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
    public async Task SkillDefinition_InstallableSkillDescriptionsFitAgentHostLimits()
    {
        var installableSkills = SkillDefinition.All
            .Where(static skill => skill.SkillContent is not null || skill.EmbeddedResourceRoot is not null);

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

    private static async Task<IReadOnlyList<SkillAssetFile>> GetInstallableSkillFilesAsync(SkillDefinition skill)
    {
        if (skill.SkillContent is not null)
        {
            return [new SkillAssetFile("SKILL.md", skill.SkillContent)];
        }

        if (skill.EmbeddedResourceRoot is not null)
        {
            return await EmbeddedSkillResourceLoader.LoadTextFilesAsync(skill.EmbeddedResourceRoot, skill.ShouldInstallFile, CancellationToken.None);
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
