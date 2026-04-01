// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

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
    public void SkillDefinition_AllDefaultsAreTrue()
    {
        Assert.All(SkillDefinition.All, s => Assert.True(s.IsDefault));
    }

    [Fact]
    public void SkillDefinition_PlaywrightCli_HasNoSkillContent()
    {
        Assert.Null(SkillDefinition.PlaywrightCli.SkillContent);
    }

    [Fact]
    public void SkillDefinition_AspireAndDotnetInspect_HaveSkillContent()
    {
        Assert.NotNull(SkillDefinition.Aspire.SkillContent);
        Assert.NotNull(SkillDefinition.DotnetInspect.SkillContent);
        Assert.Contains("# Aspire Skill", SkillDefinition.Aspire.SkillContent);
        Assert.Contains("# dotnet-inspect", SkillDefinition.DotnetInspect.SkillContent);
    }
}
