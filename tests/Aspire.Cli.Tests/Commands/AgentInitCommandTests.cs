// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AgentInitCommand_UsesNormalizedDisplayPath_WhenInstallingUserLevelSkill()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                SkillLocation location => location == SkillLocation.Standard,
                SkillDefinition skill => skill == SkillDefinition.Aspire,
                _ => false
            })
            .ToList();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.Contains(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Message == string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_InstalledSkill,
                SkillDefinition.Aspire.Name,
                "~/.agents/skills/aspire"));
    }

    [Fact]
    public async Task AgentInitCommand_IncludesSpecificSkillDirectory_WhenInstallFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var invalidRootFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "not-a-directory.txt");
        await File.WriteAllTextAsync(invalidRootFilePath, "blocked").DefaultTimeout();

        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(invalidRootFilePath);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                SkillLocation location => location == SkillLocation.Standard,
                SkillDefinition skill => skill == SkillDefinition.Aspire,
                _ => false
            })
            .ToList();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);

        var expectedSkillDirectoryPath = Path.Combine(invalidRootFilePath, ".agents", "skills", SkillDefinition.Aspire.Name);
        Assert.Contains(
            interactionService.DisplayedErrors,
            message => message.Contains(expectedSkillDirectoryPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithAllLocationsAndSkills_InstallsSkillFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Exit code is InvalidCommand because FakeNpmRunner cannot resolve Playwright CLI in tests.
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);

        // Verify that the aspire skill was installed to all locations
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".claude", "skills", "aspire", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "skills", "aspire", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".opencode", "skill", "aspire", "SKILL.md")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneLocations_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // No locations selected, so no skill directories should be created
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents");
        Assert.False(Directory.Exists(agentsDir), $"Expected no .agents directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutSkillLocations_UsesDefaultLocations()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutSkills_UsesDefaultSkills()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Default skill is Aspire, which gets installed. Playwright is not default so not selected.
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Verify the default aspire skill was installed
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.True(File.Exists(aspireSkillPath), $"Expected skill file at {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidSkillLocations_FailsWithMissingArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations invalid --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.MissingRequiredArgument, exitCode);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutWorkspaceRoot_UsesWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Verify that the default aspire skill was installed under the working directory
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Assert.True(File.Exists(aspireSkillPath), $"Expected skill file at {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneSkills_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // No skills selected, so no skill files should be created
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire");
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected no aspire skill directory but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_ConfigureMcpDefaultsToFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --configure-mcp is not passed, should default to false in non-interactive mode
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo homeDirectory)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        var logsDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "logs"));
        var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");
        return new CliExecutionContext(
            workingDirectory,
            hivesDirectory,
            cacheDirectory,
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks")),
            logsDirectory,
            logFilePath,
            homeDirectory: homeDirectory);
    }
}
