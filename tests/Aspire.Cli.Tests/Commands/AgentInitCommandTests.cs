// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AgentInitCommand_DoesNotCheckWorkspaceSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(
            new ConfigSource { Repository = "https://git.example/configuration", Path = "aspire.config.json", AppliedVersion = "version-1" },
            workspace.WorkspaceRoot, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root \"{workspace.Path}\" --agents copilot --skills aspire --scope project --mcp false --non-interactive");

        Assert.Equal(CliExitCodes.Success, await command.InvokeAsync().DefaultTimeout());

        var git = Assert.IsType<TestConfigSourceGitClient>(provider.GetRequiredService<IConfigSourceGitClient>());
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Fact]
    public async Task AgentInitCommand_SummarizesNormalizedDisplayPath_WhenInstallingUserLevelSkill()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                AgentClient client => client == AgentClient.Copilot,
                SkillDefinition skill => skill.HasName(CommonAgentApplicators.AspireSkillName),
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
        var result = command.Parse("agent init --agents copilot --scope global");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        var expectedSummary = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills, CommonAgentApplicators.AspireSkillName)}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, "~/.agents/skills")}");

        Assert.Contains(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Emoji.Equals(KnownEmojis.Robot) && displayedMessage.Message == expectedSummary);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Message.Contains("Installed aspire skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_SummarizesDefaultSkillsOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init --agents copilot");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);

        var expectedSummary = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                $"{CommonAgentApplicators.AspireSkillName}, {CommonAgentApplicators.AspireDeploymentSkillName}, {FakeAspireSkillsInstaller.AspireInitSkillName}, {FakeAspireSkillsInstaller.AspireMonitoringSkillName}, {FakeAspireSkillsInstaller.AspireOrchestrationSkillName}, {FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName}, {CommonAgentApplicators.AspireifySkillName}")}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, ".agents/skills")}");
        var message = Assert.Single(interactionService.DisplayedMessages, displayedMessage => displayedMessage.Emoji.Equals(KnownEmojis.Robot));
        Assert.Equal(expectedSummary, message.Message);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Message.Contains("Installed aspire skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_IncludesSpecificSkillDirectory_WhenInstallFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var blockedSkillParentPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents");
        await File.WriteAllTextAsync(blockedSkillParentPath, "blocked").DefaultTimeout();

        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                AgentClient client => client == AgentClient.Copilot,
                SkillDefinition skill => skill.HasName(CommonAgentApplicators.AspireSkillName),
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

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.ValidationFailures);

        var expectedSkillDirectoryPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.Contains(
            interactionService.DisplayedErrors,
            message => message.Contains(expectedSkillDirectoryPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithAllClientsAndSkills_InstallsSkillFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Exit code is InvalidCommand because FakeNpmRunner cannot resolve Playwright CLI in tests.
        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);

        var expectedSkillNames = new[]
        {
            CommonAgentApplicators.AspireSkillName,
            CommonAgentApplicators.AspireifySkillName,
            CommonAgentApplicators.AspireDeploymentSkillName,
            FakeAspireSkillsInstaller.AspireInitSkillName,
            FakeAspireSkillsInstaller.AspireMonitoringSkillName,
            FakeAspireSkillsInstaller.AspireOrchestrationSkillName,
            FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName
        };
        var expectedSkillDirectories = new[]
        {
            Path.Combine(".agents", "skills"),
            Path.Combine(".claude", "skills")
        };

        foreach (var relativeSkillDirectory in expectedSkillDirectories)
        {
            foreach (var skillName in expectedSkillNames)
            {
                AssertSkillFileExists(workspace.WorkspaceRoot, relativeSkillDirectory, skillName);
            }
        }
    }

    [Fact]
    public async Task AgentInitCommand_InteractiveSkillPrompt_IncludesAllBundleSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var promptedSkillNames = new List<string>();
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentClient)
            {
                return [AgentClient.Copilot];
            }

            promptedSkillNames.AddRange(items.OfType<SkillDefinition>().Select(static skill => skill.Name));
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(CommonAgentApplicators.AspireSkillName, promptedSkillNames);
        Assert.Contains(CommonAgentApplicators.AspireifySkillName, promptedSkillNames);
        Assert.Contains(CommonAgentApplicators.AspireDeploymentSkillName, promptedSkillNames);
        Assert.Contains(FakeAspireSkillsInstaller.AspireInitSkillName, promptedSkillNames);
        Assert.Contains(FakeAspireSkillsInstaller.AspireMonitoringSkillName, promptedSkillNames);
        Assert.Contains(FakeAspireSkillsInstaller.AspireOrchestrationSkillName, promptedSkillNames);
        Assert.Contains(FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName, promptedSkillNames);
    }

    [Fact]
    public async Task AgentInitCommand_InteractiveSkillPrompt_EscapesBundleDescriptions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bundle = await CreateBundleAsync(
            workspace.WorkspaceRoot,
            (FakeAspireSkillsInstaller.AspireMonitoringSkillName, "Observe [danger] apps"));
        string? formattedSkill = null;
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, formatter, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentClient)
            {
                return [AgentClient.Copilot];
            }

            var skill = Assert.Single(items.OfType<SkillDefinition>(), static skill => skill.HasName(FakeAspireSkillsInstaller.AspireMonitoringSkillName));
            formattedSkill = formatter(skill);
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.NotNull(formattedSkill);
        Assert.Contains("Observe [[danger]] apps", formattedSkill);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("Short description", "Short description")]
    [InlineData("Short description.", "Short description.")]
    [InlineData("First sentence. Second sentence.", "First sentence.")]
    [InlineData("**WORKFLOW SKILL** - Top-level router for Aspire 13.4 distributed apps. Detects the AppHost.", "Top-level router for Aspire 13.4 distributed apps.")]
    [InlineData("**ANALYSIS SKILL** — Observe Aspire apps. USE FOR: aspire logs.", "Observe Aspire apps.")]
    [InlineData("**SETUP SKILL**: One-time setup of resources. INVOKES: aspire add.", "One-time setup of resources.")]
    [InlineData("Visit github.com for docs. Then run the tool.", "Visit github.com for docs.")]
    [InlineData("**TYPE** -", "")]
    // Fix 1 regression: a leading separator that does NOT follow a "**TYPE**" prefix must be preserved.
    // The earlier implementation unconditionally trimmed leading separators after the bold-prefix
    // branch, which silently mutated bundle descriptions that happened to start with '-' or ':'.
    [InlineData("-Quickly do X.", "-Quickly do X.")]
    [InlineData(":memo notes", ":memo notes")]
    public void SimplifyDescription_ProducesExpectedSummary(string input, string expected)
    {
        Assert.Equal(expected, AgentInitCommand.SimplifyDescription(input));
    }

    [Fact]
    public async Task AgentInitCommand_InteractiveSkillPrompt_StripsVerboseBundleDescription()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bundle = await CreateBundleAsync(
            workspace.WorkspaceRoot,
            (FakeAspireSkillsInstaller.AspireMonitoringSkillName,
             "**ANALYSIS SKILL** - Observe Aspire apps. USE FOR: aspire logs, aspire traces. INVOKES: aspire CLI."));
        string? formattedSkill = null;
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, formatter, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentClient)
            {
                return [AgentClient.Copilot];
            }

            var skill = Assert.Single(items.OfType<SkillDefinition>(), static skill => skill.HasName(FakeAspireSkillsInstaller.AspireMonitoringSkillName));
            formattedSkill = formatter(skill);
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.NotNull(formattedSkill);
        Assert.Contains("Observe Aspire apps.", formattedSkill);
        Assert.DoesNotContain("ANALYSIS SKILL", formattedSkill);
        Assert.DoesNotContain("USE FOR", formattedSkill);
        Assert.DoesNotContain("INVOKES", formattedSkill);
    }

    [Fact]
    public async Task AgentInitCommand_InteractiveSkillPrompt_OrdersSkillsAlphabetically()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Intentionally pass bundle skills in non-alphabetical order to confirm the prompt sorts deterministically.
        var bundle = await CreateBundleAsync(
            workspace.WorkspaceRoot,
            ("zeta-bundle-skill", "Zeta skill"),
            ("alpha-bundle-skill", "Alpha skill"),
            ("middle-bundle-skill", "Middle skill"));
        var promptedSkillNames = new List<string>();
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentClient)
            {
                return [AgentClient.Copilot];
            }

            promptedSkillNames.AddRange(items.OfType<SkillDefinition>().Select(static skill => skill.Name));
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.NotEmpty(promptedSkillNames);
        var sorted = promptedSkillNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, promptedSkillNames);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithSpecificBundleSkill_InstallsSkillFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills {FakeAspireSkillsInstaller.AspireMonitoringSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".claude", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "skills")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".opencode", "skill")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_NonInteractive_InstallsProjectV2MigrationSkillWithReferences(bool selectByName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
        });
        using var provider = services.BuildServiceProvider();

        var installer = provider.GetRequiredService<IAspireSkillsInstaller>();
        var installation = await installer.InstallAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotNull(installation.Bundle);
        var skill = Assert.Single(
            installation.Bundle.GetSkillDefinitions(),
            skill => skill.HasName(FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName));
        Assert.True(skill.IsDefault);
        Assert.Empty(skill.ApplicableLanguages);
        var expectedFiles = await installation.Bundle.GetSkillFilesAsync(skill, TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal(
            [
                "SKILL.md",
                Path.Combine("references", "compatibility-and-validation.md"),
                Path.Combine("references", "migration-patterns.md")
            ],
            expectedFiles.Select(file => file.RelativePath));

        var command = provider.GetRequiredService<RootCommand>();
        var selection = selectByName ? $" --skills {skill.Name}" : string.Empty;
        var result = command.Parse($"agent init --workspace-root \"{workspace.WorkspaceRoot.FullName}\" --agents all --scope project{selection}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var skillDirectories = new[]
        {
            Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", skill.Name),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".claude", "skills", skill.Name)
        };
        foreach (var skillDirectory in skillDirectories)
        {
            Assert.Equal(
                expectedFiles.Select(file => file.RelativePath),
                Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(skillDirectory, path))
                    .Order(StringComparer.Ordinal));
            foreach (var file in expectedFiles)
            {
                Assert.Equal(
                    file.Content,
                    await File.ReadAllTextAsync(Path.Combine(skillDirectory, file.RelativePath), TestContext.Current.CancellationToken).DefaultTimeout());
            }
        }
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithCliDefinedSkillDifferentCasing_DoesNotResolveBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire skills bundle is unavailable.";
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Failed(installFailureMessage));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills PLAYWRIGHT-CLI");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) && message.Message == installFailureMessage);
    }

    [Fact]
    public async Task AgentInitCommand_InteractiveSkillPrompt_CliDefinedSkillsWinBundleNameCollisions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bundle = await CreateBundleAsync(
            workspace.WorkspaceRoot,
            (CommonAgentApplicators.AspireSkillName, "Aspire CLI commands and workflows for distributed apps"),
            (SkillDefinition.PlaywrightCli.Name, "Bundle-provided Playwright collision"));
        var promptedSkills = new List<SkillDefinition>();
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentClient)
            {
                return [AgentClient.Copilot];
            }

            promptedSkills.AddRange(items.OfType<SkillDefinition>());
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var playwrightSkill = Assert.Single(promptedSkills, static skill => skill.HasName(SkillDefinition.PlaywrightCli.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Same(SkillDefinition.PlaywrightCli, playwrightSkill);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoClients_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No clients selected, so no skill directories should be created.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents");
        Assert.False(Directory.Exists(agentsDir), $"Expected no .agents directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutClientSelection_UsesDefaultClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutSkills_UsesDefaultSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Default Aspire skills are installed (every bundle skill, including aspireify).
        // Playwright is a CLI-defined skill that is not default.
        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireDeploymentSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireInitSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireifySkillName);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_AllBundleSkills_AreInstallableByName()
    {
        // Regression guard for the original issue: the bundle ships skills (aspire-init,
        // aspire-monitoring, aspire-orchestration) that were not surfaced by the CLI because the
        // install prompt was driven by a hardcoded list. After the refactor, the catalog comes
        // from the bundle manifest.
        //
        // The assertion is data-driven against the bundle's own manifest so this test stays
        // accurate as the fixture (or, one day, the real bundle) evolves — adding or removing
        // a skill in FakeAspireSkillsInstaller doesn't require updating the test body.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        // Prime the bundle and read the list of skills it actually surfaces. The fake
        // installer's InstallAsync is idempotent, so the subsequent CLI invocation will reuse
        // this same bundle directory.
        var installer = provider.GetRequiredService<IAspireSkillsInstaller>();
        var installResult = await installer.InstallAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotNull(installResult.Bundle);
        var bundleSkillNames = installResult.Bundle.GetSkillDefinitions().Select(static s => s.Name).ToList();
        Assert.NotEmpty(bundleSkillNames);

        // Explicit names instead of `all` keeps the assertion focused on bundle skills and
        // avoids dragging in Playwright/dotnet-inspect, which would attempt real network calls.
        var skillsArg = string.Join(",", bundleSkillNames);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills {skillsArg}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        foreach (var skillName in bundleSkillNames)
        {
            AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), skillName);
        }
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithExplicitBundleSkillName_InstallsBundleSkill()
    {
        // Regression guard: bundle-only skill names (e.g. aspire-orchestration) must be selectable
        // via --skills by name now that the catalog comes from the manifest rather than the
        // hardcoded CLI list.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills {FakeAspireSkillsInstaller.AspireOrchestrationSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected only the selected skill but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidClient_FailsValidation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents invalid --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutWorkspaceRoot_UsesWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init --agents copilot");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireDeploymentSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireInitSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireifySkillName);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithUnavailableAspireSkillsBundle_SucceedsWithoutWarningOrSelectedAspireSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire skills bundle is unavailable.";
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Failed(installFailureMessage));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.DoesNotContain(installFailureMessage, interactionService.DisplayedErrors);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) && message.Message == installFailureMessage);
        Assert.Empty(interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task PromptAndChainAsync_WithUnavailableAspireSkillsBundle_SucceedsWithoutWarningOrSelectedAspireSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire skills bundle is unavailable.";
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Failed(installFailureMessage));
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AgentInitCommand>();

        var result = await command.PromptAndChainAsync(
            interactionService,
            CliExitCodes.Success,
            workspace.WorkspaceRoot,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault(AgentConfigurationScope.Project),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(result.SelectedSkills, static skill => skill.SourceKind is SkillSourceKind.AspireSkillsBundle);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) && message.Message == installFailureMessage);
        Assert.Empty(interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task PromptAndChainAsync_PreSelectsBundleDefaultsIncludingAspireify()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AgentInitCommand>();

        // Chained agent init pre-selects every bundle-sourced skill, which is the semantic
        // `aspire init` relies on so the one-time wiring skill chains into the flow.
        var result = await command.PromptAndChainAsync(
            interactionService,
            CliExitCodes.Success,
            workspace.WorkspaceRoot,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault(AgentConfigurationScope.Project),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(result.SelectedSkills, static skill => skill.HasName(CommonAgentApplicators.AspireifySkillName));
        Assert.Contains(result.SelectedSkills, static skill => skill.HasName(FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName));
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireifySkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneSkills_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No skills selected, so no skill files should be created
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected no aspire skill directory but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_DoesNotConfigureMcpByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpTrue_ConfiguresMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --mcp");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpFalse_DoesNotConfigureMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --mcp=false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithAgentNone_DoesNotInstallAnySkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // With --agents none, no skill files should be installed even when --skills all is passed.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills");
        Assert.False(Directory.Exists(agentsDir), $"Expected no agents/skills directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithClientAndSkills_InstallsOnlySpecifiedSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --skills {CommonAgentApplicators.AspireSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);

        // aspireify was not requested, so it should not be installed.
        var aspireifySkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireifySkillName);
        Assert.False(Directory.Exists(aspireifySkillPath), $"Expected no aspireify skill directory but found {aspireifySkillPath}");
    }

    private static void AssertSkillFileExists(DirectoryInfo workspaceRoot, string relativeSkillDirectory, string skillName)
    {
        var skillPath = Path.Combine(workspaceRoot.FullName, relativeSkillDirectory, skillName, "SKILL.md");
        Assert.True(File.Exists(skillPath), $"Expected skill file at {skillPath}");
    }

    private static async Task<AspireSkillsBundle> CreateBundleAsync(DirectoryInfo workspaceRoot, params (string Name, string Description)[] skills)
    {
        var bundleDirectory = new DirectoryInfo(Path.Combine(workspaceRoot.FullName, $".test-aspire-skills-bundle-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(bundleDirectory.FullName);

        var manifestSkills = new List<SkillBundleSkill>();
        foreach (var (name, description) in skills)
        {
            var skillDirectory = Path.Combine(bundleDirectory.FullName, "skills", name);
            Directory.CreateDirectory(skillDirectory);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, $$"""
                ---
                name: {{name}}
                description: "{{description}}"
                ---

                # {{name}}
                """);

            manifestSkills.Add(new SkillBundleSkill
            {
                Name = name,
                Description = description,
                Files =
                [
                    new SkillBundleFile
                    {
                        RelativePath = "SKILL.md",
                        Sha512 = ComputeSha512(skillPath)
                    }
                ]
            });
        }

        var manifest = new SkillBundleManifest
        {
            Version = AspireSkillsInstaller.Version,
            Supports = new SkillBundleSupports
            {
                AspireCli = ">=0.0.0 <999.0.0",
                AspireSdk = ">=0.0.0 <999.0.0"
            },
            Skills = [.. manifestSkills]
        };

        var manifestJson = JsonSerializer.Serialize(manifest, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest);
        var manifestPath = Path.Combine(bundleDirectory.FullName, "skill-manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);
        return await new AspireSkillsBundleProvider().LoadAsync(
            bundleDirectory,
            CancellationToken.None);
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    [Theory]
    [InlineData(nameof(AgentEnvironment.CopilotCli), "GitHub Copilot CLI")]
    [InlineData(nameof(AgentEnvironment.CopilotApp), "GitHub Copilot App")]
    public async Task AgentInitCommand_GlobalScope_InstallsTelemetryHook_ForSelectedDetectedEnvironment(string environmentName, string displayName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        var environment = Enum.Parse<AgentEnvironment>(environmentName);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = environment };
            options.InteractionServiceFactory = _ => interactionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --skills aspire");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var hookFile = Path.Combine(homeDirectory.FullName, ".copilot", "hooks", "aspire-telemetry.json");
        Assert.True(File.Exists(hookFile), $"Expected telemetry hook at {hookFile}");
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Message.Contains(displayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_DoesNotFail_WhenTelemetryHookConfigurationThrows()
    {
        // Hook installation is best-effort transparency tooling. A non-IO failure such as a missing
        // embedded hook script (InvalidOperationException from the installer) must not abort `agent init`.
        const string failureMessage = "simulated hook configuration failure";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        var subtleMessages = new List<string>();
        interactionService.DisplaySubtleMessageCallback = subtleMessages.Add;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
            options.InteractionServiceFactory = _ => interactionService;
            options.TelemetryHookConfiguratorFactory = _ => new TestTelemetryHookConfigurator { Exception = new InvalidOperationException(failureMessage) };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --skills aspire");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(failureMessage, subtleMessages);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo homeDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: homeDirectory);
    }

}
