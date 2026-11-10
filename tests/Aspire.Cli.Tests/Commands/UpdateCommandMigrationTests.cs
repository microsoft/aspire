// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class UpdateCommandMigrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Update_MigrateWithoutAppHostReplacesSkills()
    {
        using var context = new AgentConfigurationTestContext(output);
        var skill = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        using var provider = CreateServiceProviderWithoutAppHost(context);

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("update --migrate --yes").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(skill));
    }

    [Fact]
    public async Task Update_MigrateWithoutAppHostFailsWhenMigrationFails()
    {
        using var context = new AgentConfigurationTestContext(output);
        var skill = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        await AgentConfigurationTestContext.WriteAsync(
            context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project),
            """{"enabledPlugins":{"aspire@aspire-skills":false}}""");
        using var provider = CreateServiceProviderWithoutAppHost(context);

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("update --migrate --yes").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToUpgradeProject, exitCode);
        Assert.Equal("project skill", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task Update_MigrateListsSkillDirectoriesBeforeConfirmation()
    {
        using var context = new AgentConfigurationTestContext(output);
        var skill = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        var events = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplaySubtleMessageCallback = events.Add,
            ConfirmCallback = (_, _) =>
            {
                events.Add("<confirm>");
                return false;
            }
        };
        using var provider = CreateServiceProviderWithoutAppHost(context, interaction);

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("update --migrate").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [$"  - {MigrationStrings.AgentSkillsTitle}", "    .agents/skills/aspire", "<confirm>", MigrationStrings.MigrationCancelled],
            events);
        Assert.Equal("project skill", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task Update_WithoutAppHostShowsPendingMigrations()
    {
        using var context = new AgentConfigurationTestContext(output);
        var skill = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        var subtleMessages = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplaySubtleMessageCallback = subtleMessages.Add,
            ConfirmCallback = (_, _) => false
        };
        using var provider = CreateServiceProviderWithoutAppHost(context, interaction);

        await provider.GetRequiredService<RootCommand>().Parse("update").InvokeAsync().DefaultTimeout();

        Assert.Contains((KnownEmojis.Information, UpdateCommandStrings.PendingMigrationsHeader, (ConsoleOutput?)null), interaction.DisplayedMessages);
        Assert.Equal([$"  - {MigrationStrings.AgentSkillsTitle}", UpdateCommandStrings.PendingMigrationsHint], subtleMessages);
        Assert.Equal("project skill", await File.ReadAllTextAsync(skill));
    }

    private ServiceProvider CreateServiceProviderWithoutAppHost(AgentConfigurationTestContext context, TestInteractionService? interaction = null)
        => context.CreateServiceCollection(output, options =>
        {
            if (interaction is not null)
            {
                options.InteractionServiceFactory = _ => interaction;
            }

            options.GitRepositoryFactory = _ => new TestGitRepository();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) =>
                    throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound)
            };
        }).BuildServiceProvider();
}
