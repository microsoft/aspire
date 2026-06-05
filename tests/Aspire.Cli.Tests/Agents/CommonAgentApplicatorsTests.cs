// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TryAddAgentInstructionsApplicator_WhenNotYetAdded_AddsApplicatorAndReturnsTrue()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        // Act
        var result = CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, workspace.WorkspaceRoot);

        // Assert
        Assert.True(result);
        Assert.True(context.AgentInstructionsApplicatorAdded);
        Assert.Single(context.Applicators);
    }

    [Fact]
    public void TryAddAgentInstructionsApplicator_WhenAlreadyAdded_DoesNotAddAgainAndReturnsFalse()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        context.AgentInstructionsApplicatorAdded = true;

        // Act
        var result = CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, workspace.WorkspaceRoot);

        // Assert
        Assert.False(result);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public void TryAddAgentInstructionsApplicator_WhenAgentsMdExists_DoesNotAddApplicator()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        // Create AGENTS.md with any content
        var agentsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "AGENTS.md");
        File.WriteAllText(agentsFilePath, "# Existing Content\n\nThis already exists.");

        // Act
        var result = CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, workspace.WorkspaceRoot);

        // Assert - should not add applicator since AGENTS.md already exists
        Assert.False(result);
        Assert.True(context.AgentInstructionsApplicatorAdded);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task CreateAgentInstructionsAsync_CreatesAgentsMdWhenItDoesNotExist()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        // Act
        CommonAgentApplicators.TryAddAgentInstructionsApplicator(context, workspace.WorkspaceRoot);
        await context.Applicators[0].ApplyAsync(CancellationToken.None);

        // Assert
        var agentsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "AGENTS.md");
        Assert.True(File.Exists(agentsFilePath));
        var content = await File.ReadAllTextAsync(agentsFilePath);
        Assert.Contains("# Copilot instructions", content);
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
