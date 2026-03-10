// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests(ITestOutputHelper outputHelper)
{
    private const string TestSkillRelativePath = ".github/skills/aspire/SKILL.md";
    private const string TestDotnetInspectSkillRelativePath = ".github/skills/dotnet-inspect/SKILL.md";
    private const string TestDescription = "Create Aspire skill file";
    private const string TestDotnetInspectDescription = "Create dotnet-inspect skill file";

    [Fact]
    public void TryAddSkillFileApplicator_WhenNotYetAdded_AddsApplicatorAndReturnsTrue()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        // Act
        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);

        // Assert
        Assert.True(result);
        Assert.True(context.HasSkillFileApplicator(TestSkillRelativePath));
        Assert.Single(context.Applicators);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WhenAlreadyAdded_DoesNotAddAgainAndReturnsFalse()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        context.MarkSkillFileApplicatorAdded(TestSkillRelativePath);

        // Act
        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);

        // Assert
        Assert.False(result);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WhenSkillFileExistsWithSameContent_DoesNotAddApplicator()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        // Create the skill file with the SAME content as SkillFileContent
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestSkillRelativePath);
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(skillFilePath, CommonAgentApplicators.SkillFileContent);

        // Act
        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);

        // Assert - should not add applicator since skill file already exists with same content
        Assert.False(result);
        Assert.True(context.HasSkillFileApplicator(TestSkillRelativePath));
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WhenSkillFileExistsWithDifferentContent_AddsUpdateApplicator()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        // Create the skill file with DIFFERENT content
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestSkillRelativePath);
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(skillFilePath, "# Old Aspire Skill\n\nThis is outdated content.");

        // Act
        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);

        // Assert - should add an update applicator since content differs
        Assert.True(result);
        Assert.True(context.HasSkillFileApplicator(TestSkillRelativePath));
        Assert.Single(context.Applicators);
        Assert.Contains("update", context.Applicators[0].Description);
    }

    [Fact]
    public async Task TryAddSkillFileApplicator_UpdateApplicator_ReplacesContent()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        // Create the skill file with old content
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestSkillRelativePath);
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        Directory.CreateDirectory(skillDirectory);
        var oldContent = "# Old Aspire Skill\n\nThis is outdated content.";
        File.WriteAllText(skillFilePath, oldContent);

        // Act
        CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);
        await context.Applicators[0].ApplyAsync(CancellationToken.None).DefaultTimeout();

        // Assert - content should be replaced with new content
        var newContent = await File.ReadAllTextAsync(skillFilePath);
        Assert.NotEqual(oldContent, newContent);
        Assert.Equal(CommonAgentApplicators.SkillFileContent, newContent);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WhenSkillFileExistsWithDifferentLineEndings_DoesNotAddApplicator()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        // Create the skill file with CRLF line endings (Windows style)
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestSkillRelativePath);
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        Directory.CreateDirectory(skillDirectory);
        var contentWithCrlf = CommonAgentApplicators.SkillFileContent.ReplaceLineEndings("\r\n");
        File.WriteAllText(skillFilePath, contentWithCrlf);

        // Act
        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);

        // Assert - should not add applicator since content is the same (just different line endings)
        Assert.False(result);
        Assert.True(context.HasSkillFileApplicator(TestSkillRelativePath));
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task TryAddSkillFileApplicator_CreatesSkillFileWhenItDoesNotExist()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        // Act
        CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);
        await context.Applicators[0].ApplyAsync(CancellationToken.None).DefaultTimeout();

        // Assert
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestSkillRelativePath);
        Assert.True(File.Exists(skillFilePath));
        var content = await File.ReadAllTextAsync(skillFilePath);
        Assert.Contains("# Aspire Skill", content);
        Assert.Contains("aspire start --isolated", content);
    }

    [Fact]
    public void TryAddSkillFileApplicator_DifferentPaths_AddsBothApplicators()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        var path1 = ".github/skills/aspire/SKILL.md";
        var path2 = ".claude/skills/aspire/SKILL.md";

        // Act
        var result1 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            path1,
            "Description 1");
        var result2 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            path2,
            "Description 2");

        // Assert - both should be added since they are different paths
        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, context.Applicators.Count);
    }

    [Fact]
    public void TryAddSkillFileApplicator_SamePathTwice_OnlyAddsOnce()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        var path = ".github/skills/aspire/SKILL.md";

        // Act - try to add the same path twice (simulating VS Code and Copilot CLI both trying to add)
        var result1 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            path,
            "Description 1");
        var result2 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            path,
            "Description 2");

        // Assert - only the first should be added
        Assert.True(result1);
        Assert.False(result2);
        Assert.Single(context.Applicators);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WithCustomContent_AddsApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestDotnetInspectSkillRelativePath,
            TestDotnetInspectDescription,
            CommonAgentApplicators.DotnetInspectSkillFileContent);

        Assert.True(result);
        Assert.True(context.HasSkillFileApplicator(TestDotnetInspectSkillRelativePath));
        Assert.Single(context.Applicators);
    }

    [Fact]
    public async Task TryAddSkillFileApplicator_WithCustomContent_CreatesCorrectFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestDotnetInspectSkillRelativePath,
            TestDotnetInspectDescription,
            CommonAgentApplicators.DotnetInspectSkillFileContent);
        await context.Applicators[0].ApplyAsync(CancellationToken.None).DefaultTimeout();

        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestDotnetInspectSkillRelativePath);
        Assert.True(File.Exists(skillFilePath));
        var content = await File.ReadAllTextAsync(skillFilePath);
        Assert.Contains("# dotnet-inspect", content);
        Assert.Contains("dnx dotnet-inspect", content);
    }

    [Fact]
    public void TryAddSkillFileApplicator_AspireAndDotnetInspect_AddsBothApplicators()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var result1 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestSkillRelativePath,
            TestDescription);
        var result2 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestDotnetInspectSkillRelativePath,
            TestDotnetInspectDescription,
            CommonAgentApplicators.DotnetInspectSkillFileContent);

        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, context.Applicators.Count);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WithCustomContent_WhenSameContentExists_DoesNotAddApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, TestDotnetInspectSkillRelativePath);
        var skillDirectory = Path.GetDirectoryName(skillFilePath)!;
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(skillFilePath, CommonAgentApplicators.DotnetInspectSkillFileContent);

        var result = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            TestDotnetInspectSkillRelativePath,
            TestDotnetInspectDescription,
            CommonAgentApplicators.DotnetInspectSkillFileContent);

        Assert.False(result);
        Assert.True(context.HasSkillFileApplicator(TestDotnetInspectSkillRelativePath));
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public void TryAddSkillFileApplicator_WithDeduplicationKey_UsesKeyForDedup()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        var relativePath = ".agents/skills/aspire/SKILL.md";

        // Add with default dedup key (relative path)
        var result1 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            relativePath,
            "Workspace skill");

        // Add same relative path but with a different dedup key (simulates user-level)
        var result2 = CommonAgentApplicators.TryAddSkillFileApplicator(
            context,
            workspace.WorkspaceRoot,
            relativePath,
            "User skill",
            deduplicationKey: "user:" + relativePath);

        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, context.Applicators.Count);
    }

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

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo workingDirectory)
    {
        return new AgentEnvironmentScanContext
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = workingDirectory
        };
    }
}
