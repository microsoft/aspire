// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Commands;
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
    public async Task AgentInitCommand_SummarizesNormalizedDisplayPath_WhenInstallingUserLevelSkill()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                IAgentClient client => client.Id == "copilot",
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
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                $"{CommonAgentApplicators.AspireSkillName}, {CommonAgentApplicators.AspireDeploymentSkillName}, {FakeAspireSkillsInstaller.AspireInitSkillName}, {FakeAspireSkillsInstaller.AspireMonitoringSkillName}, {FakeAspireSkillsInstaller.AspireOrchestrationSkillName}, {FakeAspireSkillsInstaller.AspireProjectV2MigrationSkillName}, {CommonAgentApplicators.AspireifySkillName}")}",
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
                IAgentClient client => client.Id == "copilot",
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills bundled --playwright --dotnet-inspect");

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_NonInteractive_InstallsProjectV2MigrationSkillWithReferences(bool explicitBundled)
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
        var selection = explicitBundled ? " --aspire-skills bundled" : string.Empty;
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
    public async Task AgentInitCommand_NonInteractive_WithNoClients_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --aspire-skills bundled");

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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --aspire-skills none");

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
    public async Task AgentInitCommand_NonInteractive_BundledInstallsEveryApplicableManifestSkill()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var installer = provider.GetRequiredService<IAspireSkillsInstaller>();
        var installResult = await installer.InstallAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotNull(installResult.Bundle);
        var bundleSkillNames = installResult.Bundle.GetSkillDefinitions().Select(static s => s.Name).ToList();
        Assert.NotEmpty(bundleSkillNames);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills bundled");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        foreach (var skillName in bundleSkillNames)
        {
            AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), skillName);
        }
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidClient_FailsValidation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents invalid --aspire-skills bundled");

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
    public async Task AgentInitCommand_NonInteractive_WithUnavailableAspireSkillsBundle_FailsExplicitly()
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

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(installFailureMessage, Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task PromptAndChainAsync_WithUnavailableAspireSkillsBundle_FailsExplicitly()
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
            PromptBinding.CreateDefault(false),
            PromptBinding.CreateDefault(false),
            PromptBinding.CreateDefault(AspireSkillsMode.Bundled),
            PromptBinding.CreateDefault(AgentConfigurationScope.Project),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Empty(result.SelectedSkills);
        Assert.Equal(installFailureMessage, Assert.Single(interactionService.DisplayedErrors));
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
            PromptBinding.CreateDefault(false),
            PromptBinding.CreateDefault(false),
            PromptBinding.CreateDefault(AspireSkillsMode.Bundled),
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills none");

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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --aspire-skills bundled");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // With --agents none, no skill files should be installed even when --aspire-skills bundled is passed.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills");
        Assert.False(Directory.Exists(agentsDir), $"Expected no agents/skills directory but found {agentsDir}");
    }

    private static void AssertSkillFileExists(DirectoryInfo workspaceRoot, string relativeSkillDirectory, string skillName)
    {
        var skillPath = Path.Combine(workspaceRoot.FullName, relativeSkillDirectory, skillName, "SKILL.md");
        Assert.True(File.Exists(skillPath), $"Expected skill file at {skillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_BundledUsesTheManifestWithoutEnablingIndependentTools()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bundle = await CreateBundleAsync(workspace.WorkspaceRoot,
            ("zeta-custom", "Additional bundle skill"), ("alpha-custom", "Another bundle skill"),
            (SkillDefinition.PlaywrightCli.Name, "Must not replace the independently selected tool"));
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = provider => new FakeAspireSkillsInstaller(
                provider.GetRequiredService<CliExecutionContext>(), AspireSkillsInstallResult.Installed(bundle));
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents copilot --aspire-skills bundled");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var directory = Path.Combine(workspace.Path, ".agents", "skills");
        Assert.Equal(["alpha-custom", "zeta-custom"], Directory.EnumerateDirectories(directory).Select(Path.GetFileName).Order());
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), "alpha-custom");
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), "zeta-custom");
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --aspire-skills bundled");

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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --aspire-skills bundled");

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
