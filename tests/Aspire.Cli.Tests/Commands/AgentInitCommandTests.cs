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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);

        var expectedSkillDirectoryPath = Path.Combine(invalidRootFilePath, ".agents", "skills", SkillDefinition.Aspire.Name);
        Assert.Contains(
            interactionService.DisplayedErrors,
            message => message.Contains(expectedSkillDirectoryPath, StringComparison.Ordinal));
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
