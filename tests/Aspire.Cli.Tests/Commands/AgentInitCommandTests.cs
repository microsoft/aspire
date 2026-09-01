// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
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
    [Theory]
    [InlineData("agent init --mcps none")]
    [InlineData("init --mcps none")]
    [InlineData("new --mcps none")]
    public void McpsOption_IsAvailableOnAgentInitInitAndNew(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(commandLine);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PromptAndChainAsync_TracksSelectionForEveryDeclaredAgentAssetKind()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                AgentAssetLocation location => location == AgentAssetLocation.Standard || location == AgentAssetLocation.ProjectExtensions,
                AgentAssetDefinition asset => asset.HasName(CommonAgentApplicators.AspireSkillName) || asset.HasName("aspire-doctor"),
                _ => false
            })
            .ToList();
        FakeAspireSkillsInstaller? installer = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ =>
                new FakeAgentEnvironmentDetector(AgentClientKind.CopilotApp);
            options.AspireSkillsInstallerFactory = serviceProvider =>
                installer = new FakeAspireSkillsInstaller(serviceProvider.GetRequiredService<CliExecutionContext>());
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        // MCP has no Aspire-skills bundle, so only the file-backed kinds reach the installer even
        // though every declared kind is tracked through its own result field below.
        Assert.Equal([AgentAssetKind.Skill, AgentAssetKind.Extension], installer!.RequestedAssetKinds);

        Assert.Equal([AgentAssetLocation.Standard], result.GetLocations(AgentAssetKind.Skill));
        var skill = Assert.Single(result.GetAssets(AgentAssetKind.Skill));
        Assert.Equal(CommonAgentApplicators.AspireSkillName, skill.Name);
        Assert.Equal([AgentAssetLocation.ProjectExtensions], result.GetLocations(AgentAssetKind.Extension));
        var extension = Assert.Single(result.GetAssets(AgentAssetKind.Extension));
        Assert.Equal("aspire-doctor", extension.Name);
        // MCP is action-backed: no applicators were detected, so nothing was offered or selected.
        Assert.Empty(result.GetAssets(AgentAssetKind.Mcp));
    }

    [Fact]
    public async Task PromptAndChainAsync_SkipsExtensionPromptsWithoutCompatibleDetectedClient()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var promptedKinds = new List<string>();
        interactionService.PromptForSelectionsCallback = (prompt, choices, _, _) =>
        {
            promptedKinds.Add(prompt);
            return choices.Cast<object>()
                .Where(choice => choice switch
                {
                    AgentAssetLocation location => location == AgentAssetLocation.Standard,
                    AgentAssetDefinition asset => asset.HasName(CommonAgentApplicators.AspireSkillName),
                    _ => false
                })
                .ToList();
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ =>
                new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.GetLocations(AgentAssetKind.Extension));
        Assert.Empty(result.GetAssets(AgentAssetKind.Extension));
        // Skill prompts still run for Copilot CLI; only the extension prompts are skipped.
        Assert.Equal(
            [AgentCommandStrings.InitCommand_SelectSkillLocations, AgentCommandStrings.InitCommand_SelectSkills],
            promptedKinds);
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) &&
                message.Message == AgentCommandStrings.InitCommand_NoDetectedClientForExtensions);
    }

    [Fact]
    public async Task PromptAndChainAsync_SkipsDefaultAssetsWithoutCompatibleDetectedClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, _, _, _) =>
                throw new InvalidOperationException("Asset prompts should be skipped.")
        };
        FakeAspireSkillsInstaller? installer = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector();
            options.AspireSkillsInstallerFactory = serviceProvider =>
                installer = new FakeAspireSkillsInstaller(serviceProvider.GetRequiredService<CliExecutionContext>());
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.GetLocations(AgentAssetKind.Skill));
        Assert.Empty(result.GetAssets(AgentAssetKind.Skill));
        Assert.Empty(result.GetLocations(AgentAssetKind.Extension));
        Assert.Empty(result.GetAssets(AgentAssetKind.Extension));
        Assert.Empty(result.GetAssets(AgentAssetKind.Mcp));
        Assert.Empty(installer!.RequestedAssetKinds);
        Assert.Collection(
            interactionService.DisplayedMessages.Where(message => message.Emoji.Equals(KnownEmojis.Warning)),
            message => Assert.Equal(AgentCommandStrings.InitCommand_NoDetectedClientForSkills, message.Message),
            message => Assert.Equal(AgentCommandStrings.InitCommand_NoDetectedClientForExtensions, message.Message),
            message => Assert.Equal(AgentCommandStrings.InitCommand_NoDetectedClientForMcp, message.Message));
    }

    [Fact]
    public async Task AgentInitCommand_ExplicitSkillSelectionsOverrideMissingClientCapability()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations standard --skills {CommonAgentApplicators.AspireSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertSkillFileExists(
            workspace.WorkspaceRoot,
            Path.Combine(".agents", "skills"),
            CommonAgentApplicators.AspireSkillName);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Message == AgentCommandStrings.InitCommand_NoDetectedClientForSkills);
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Message == AgentCommandStrings.InitCommand_NoDetectedClientForMcp);
    }

    [Fact]
    public async Task AgentInitCommand_WithoutExplicitSkillLocations_ClaudeCodeUsesClaudeDefault()
    {
        await AssertDetectedClientDefaultSkillLocationAsync(
            AgentClientKind.ClaudeCode,
            Path.Combine(".claude", "skills"),
            ".agents");
    }

    [Fact]
    public async Task AgentInitCommand_WithoutExplicitSkillLocations_VsCodeUsesStandardDefault()
    {
        await AssertDetectedClientDefaultSkillLocationAsync(
            AgentClientKind.VsCode,
            Path.Combine(".agents", "skills"),
            ".github");
    }

    [Fact]
    public async Task PromptAndChainAsync_SelectingMcpAsset_AppliesEachDistinctDetectedTargetOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var applyCounts = new int[5];
        AgentEnvironmentApplicator CreateApplicator(string description, string targetId, int index)
            => AgentEnvironmentApplicator.ForAsset(
                AgentAssetCatalog.AspireMcpServer,
                targetId,
                description,
                _ =>
                {
                    applyCounts[index]++;
                    return Task.CompletedTask;
                });

        var interactionService = new TestInteractionService();
        interactionService.PromptForSelectionsCallback = (prompt, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentAssetLocation)
            {
                return [];
            }

            Assert.Equal(AgentCommandStrings.InitCommand_SelectMcpServers, prompt);
            var mcpAsset = Assert.Single(items.OfType<AgentActionAssetDefinition>());
            Assert.Same(AgentAssetCatalog.AspireMcpServer, mcpAsset);
            return [mcpAsset];
        };
        var detector = new FakeAgentEnvironmentDetector(
            AgentClientKind.CopilotCli,
            AgentClientKind.CopilotApp,
            AgentClientKind.VsCode,
            AgentClientKind.ClaudeCode,
            AgentClientKind.OpenCode)
        {
            Applicators =
            [
                CreateApplicator("Copilot CLI MCP", "copilot", 0),
                CreateApplicator("Copilot App MCP", "copilot", 1),
                CreateApplicator("VS Code MCP", "vscode", 2),
                CreateApplicator("Claude Code MCP", "claude", 3),
                CreateApplicator("OpenCode MCP", "opencode", 4),
            ]
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            selectByDefault: null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        // The Copilot CLI and Copilot App share one MCP configuration file, so detection collapses
        // them into a single target that must only be written once.
        Assert.Equal([1, 0, 1, 1, 1], applyCounts);
        Assert.Equal([AgentAssetCatalog.AspireMcpServer], result.GetAssets(AgentAssetKind.Mcp));
    }

    [Fact]
    public async Task PromptAndChainAsync_WhenMcpTargetFails_ContinuesWithRemainingTargets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var successfulApplyCount = 0;
        var interactionService = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) =>
            {
                var items = choices.Cast<object>().ToList();
                return items.FirstOrDefault() is AgentAssetLocation
                    ? []
                    : [Assert.Single(items.OfType<AgentActionAssetDefinition>())];
            }
        };
        var detector = new FakeAgentEnvironmentDetector(AgentClientKind.VsCode, AgentClientKind.OpenCode)
        {
            Applicators =
            [
                AgentEnvironmentApplicator.ForAsset(
                    AgentAssetCatalog.AspireMcpServer,
                    "vscode",
                    "Unwritable VS Code MCP",
                    _ => throw new IOException("Configuration is read-only.")),
                AgentEnvironmentApplicator.ForAsset(
                    AgentAssetCatalog.AspireMcpServer,
                    "opencode",
                    "OpenCode MCP",
                    _ =>
                    {
                        successfulApplyCount++;
                        return Task.CompletedTask;
                    }),
            ]
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => detector;
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            selectByDefault: null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal(1, successfulApplyCount);
        Assert.Contains("Configuration is read-only.", interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task PromptAndChainAsync_WhenCopilotAppIsDetected_InstallsExtensionAndAppliesSharedMcpTargetOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applyCount = 0;
        AgentEnvironmentApplicator CreateApplicator(string description)
            => AgentEnvironmentApplicator.ForAsset(
                AgentAssetCatalog.AspireMcpServer,
                // The Copilot CLI and the Copilot App write the same ~/.copilot/mcp-config.json, so
                // both scanners contribute the same target id and detection collapses them into one.
                targetId: "copilot",
                description,
                _ =>
                {
                    applyCount++;
                    return Task.CompletedTask;
                });

        var interactionService = new TestInteractionService();
        var promptedKinds = new List<string>();
        interactionService.PromptForSelectionsCallback = (prompt, choices, _, _) =>
        {
            promptedKinds.Add(prompt);
            return choices.Cast<object>()
                .Where(choice => choice switch
                {
                    AgentAssetLocation location => location == AgentAssetLocation.ProjectExtensions,
                    AgentAssetDefinition asset =>
                        asset == AgentAssetCatalog.AspireMcpServer || asset.HasName("aspire-doctor"),
                    _ => false
                })
                .ToList();
        };
        var detector = new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli, AgentClientKind.CopilotApp)
        {
            Applicators =
            [
                CreateApplicator("Copilot CLI MCP"),
                CreateApplicator("Copilot App MCP"),
            ]
        };
        FakeAspireSkillsInstaller? installer = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.AspireSkillsInstallerFactory = serviceProvider =>
                installer = new FakeAspireSkillsInstaller(serviceProvider.GetRequiredService<CliExecutionContext>());
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            selectByDefault: null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        // The Copilot App is the only client supporting both kinds, so the extension prompt and the
        // separate MCP prompt both run in the same invocation.
        Assert.Contains(AgentCommandStrings.InitCommand_SelectMcpServers, promptedKinds);
        Assert.Contains(AgentCommandStrings.InitCommand_SelectExtensions, promptedKinds);
        Assert.Equal(1, applyCount);
        // MCP applicators are bound to the exact asset definition they configure, so the shared
        // target is applied once for the selected AspireMcpServer instance and reported explicitly.
        Assert.Equal([AgentAssetCatalog.AspireMcpServer], result.GetAssets(AgentAssetKind.Mcp));
        Assert.Equal([AgentAssetLocation.ProjectExtensions], result.GetLocations(AgentAssetKind.Extension));
        var extension = Assert.Single(result.GetAssets(AgentAssetKind.Extension));
        Assert.Equal("aspire-doctor", extension.Name);
        // MCP is applied through detected-environment actions, so it must never request a bundle.
        Assert.Equal([AgentAssetKind.Extension], installer!.RequestedAssetKinds);
        var extensionPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".github",
            "extensions",
            "aspire-doctor",
            "extension.mjs");
        Assert.True(File.Exists(extensionPath), $"Expected extension at {extensionPath}");
    }

    [Fact]
    public async Task PromptAndChainAsync_WhenMcpTargetFails_KeepsInstalledExtensionFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
                .Where(choice => choice switch
                {
                    AgentAssetLocation location => location == AgentAssetLocation.ProjectExtensions,
                    AgentAssetDefinition asset =>
                        asset == AgentAssetCatalog.AspireMcpServer || asset.HasName("aspire-doctor"),
                    _ => false
                })
                .ToList()
        };
        var detector = new FakeAgentEnvironmentDetector(AgentClientKind.CopilotApp)
        {
            Applicators =
            [
                AgentEnvironmentApplicator.ForAsset(
                    AgentAssetCatalog.AspireMcpServer,
                    targetId: "copilot",
                    "Unwritable Copilot App MCP",
                    _ => throw new IOException("Configuration is read-only.")),
            ]
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => detector;
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            selectByDefault: null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        // Extensions are installed through a staged file tree that rolls back on its own failures.
        // A failing MCP target is a separate concern: it degrades the exit code but must never undo
        // or block the extension files that were already committed.
        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Contains("Configuration is read-only.", interactionService.DisplayedErrors);
        Assert.Equal(
            [
                "extension.mjs",
                Path.Combine("ui", "icon.bin"),
            ],
            Directory.EnumerateFiles(
                    Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor"),
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(
                    Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor"),
                    path))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PromptAndChainAsync_InstallsCliDefinedAsset_WhenKindHasNoBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            TestContext.Current.CancellationToken);
        var interactionService = new TestInteractionService();
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
            .Where(choice => choice switch
            {
                AgentAssetLocation location => location == AgentAssetLocation.Standard,
                AgentAssetDefinition asset => asset == AgentAssetCatalog.DotnetInspect,
                _ => false
            })
            .ToList();
        FakeAspireSkillsInstaller? installer = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
            options.AspireSkillsInstallerFactory = serviceProvider =>
                installer = new FakeAspireSkillsInstaller(
                    serviceProvider.GetRequiredService<CliExecutionContext>(),
                    AspireSkillsInstallResult.Unavailable);
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.NotNull(installer);
        Assert.Equal([AgentAssetKind.Skill], installer.RequestedAssetKinds);
        Assert.Equal([AgentAssetCatalog.DotnetInspect], result.GetAssets(AgentAssetKind.Skill));
        AssertSkillFileExists(
            workspace.WorkspaceRoot,
            Path.Combine(".agents", "skills"),
            CommonAgentApplicators.DotnetInspectSkillName);
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
                AgentAssetLocation location => location == AgentAssetLocation.Standard,
                AgentAssetDefinition asset => asset.HasName(CommonAgentApplicators.AspireSkillName),
                _ => false
            })
            .ToList();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        var expectedSummary = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills, CommonAgentApplicators.AspireSkillName)}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, ".agents/skills, ~/.agents/skills")}");

        Assert.Contains(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Emoji.Equals(KnownEmojis.Robot) && displayedMessage.Message == expectedSummary);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Message.Contains("Installed aspire skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_ExtensionOptionsInstallProjectExtension()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var extensionPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".github",
            "extensions",
            "aspire-doctor",
            "extension.mjs");
        Assert.True(File.Exists(extensionPath), $"Expected extension at {extensionPath}");
        var binaryPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".github",
            "extensions",
            "aspire-doctor",
            "ui",
            "icon.bin");
        var binaryContent = await File.ReadAllBytesAsync(binaryPath);
        Assert.Equal<byte>([0x00, 0xff, 0x80, 0x0a], binaryContent);
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) &&
                message.Message == AgentCommandStrings.InitCommand_NoCompatibleClientForExplicitExtensions);
    }

    [Fact]
    public async Task AgentInitCommand_ExtensionUpgradeRemovesFilesMissingFromBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var extensionDirectory = workspace.CreateDirectory(
            Path.Combine(".github", "extensions", "aspire-doctor"));
        await File.WriteAllTextAsync(
            Path.Combine(extensionDirectory.FullName, "obsolete.mjs"),
            "obsolete",
            TestContext.Current.CancellationToken);
        var obsoleteUiDirectory = extensionDirectory.CreateSubdirectory("obsolete-ui");
        await File.WriteAllTextAsync(
            Path.Combine(obsoleteUiDirectory.FullName, "old.js"),
            "obsolete",
            TestContext.Current.CancellationToken);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [
                "extension.mjs",
                Path.Combine("ui", "icon.bin"),
            ],
            Directory.EnumerateFiles(extensionDirectory.FullName, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extensionDirectory.FullName, path))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Validates case-sensitive Linux extension paths.")]
    public async Task AgentInitCommand_ExtensionUpgradeRemovesDifferentlyCasedStaleFileOnLinux()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var extensionDirectory = workspace.CreateDirectory(
            Path.Combine(".github", "extensions", "aspire-doctor"));
        var differentlyCasedDirectory = extensionDirectory.CreateSubdirectory("UI");
        await File.WriteAllBytesAsync(
            Path.Combine(differentlyCasedDirectory.FullName, "icon.bin"),
            [0x01, 0x02, 0x03],
            TestContext.Current.CancellationToken);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [
                "extension.mjs",
                Path.Combine("ui", "icon.bin"),
            ],
            Directory.EnumerateFiles(extensionDirectory.FullName, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extensionDirectory.FullName, path))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.FreeBSD, "Requires a case-insensitive file system.")]
    public async Task AgentInitCommand_ExtensionUpgradeKeepsDifferentlyCasedCurrentFileOnMacOS()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var extensionDirectory = workspace.CreateDirectory(
            Path.Combine(".github", "extensions", "aspire-doctor"));
        var differentlyCasedDirectory = extensionDirectory.CreateSubdirectory("UI");
        var differentlyCasedPath = Path.Combine(differentlyCasedDirectory.FullName, "icon.bin");
        await File.WriteAllBytesAsync(
            differentlyCasedPath,
            [0x01, 0x02, 0x03],
            TestContext.Current.CancellationToken);

        Assert.SkipUnless(
            File.Exists(Path.Combine(extensionDirectory.FullName, "ui", "icon.bin")),
            "This test requires a case-insensitive macOS volume.");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        services.AddSingleton<IEnvironment>(TestEnvironment.CreateMacOS());
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        var updatedContent = await File.ReadAllBytesAsync(
            differentlyCasedPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal<byte>([0x00, 0xff, 0x80, 0x0a], updatedContent);
        Assert.Equal(2, Directory.EnumerateFiles(extensionDirectory.FullName, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task PromptAndChainAsync_UnavailableExtensionBundleDoesNotBlockSkillInstallation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var materializedChoices = choices.Cast<object>().ToList();
            Assert.NotEmpty(materializedChoices);
            return materializedChoices
                .Where(choice => choice switch
                {
                    AgentAssetLocation location => location == AgentAssetLocation.Standard ||
                        location == AgentAssetLocation.ProjectExtensions,
                    AgentAssetDefinition asset => asset.HasName(CommonAgentApplicators.AspireSkillName),
                    _ => false
                })
                .ToList();
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotApp);
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null)
            {
                ExtensionResult = AspireSkillsInstallResult.Unavailable
            };
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.GetAssets(AgentAssetKind.Extension));
        AssertSkillFileExists(
            workspace.WorkspaceRoot,
            Path.Combine(".agents", "skills"),
            CommonAgentApplicators.AspireSkillName);
    }

    [Fact]
    public async Task AgentInitCommand_ExtensionOptionsInstallUserExtensionUnderHomeDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations user --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var extensionPath = Path.Combine(
            homeDirectory.FullName,
            ".copilot",
            "extensions",
            "aspire-doctor",
            "extension.mjs");
        Assert.True(File.Exists(extensionPath), $"Expected extension at {extensionPath}");
    }

    [Fact]
    public async Task AgentInitCommand_ExtensionOptionsInstallUserExtensionUnderCopilotHome()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var copilotHomeDirectory = workspace.CreateDirectory("copilot-home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = copilotHomeDirectory.FullName,
        }));
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations user --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var extensionPath = Path.Combine(
            copilotHomeDirectory.FullName,
            "extensions",
            "aspire-doctor",
            "extension.mjs");
        Assert.True(File.Exists(extensionPath), $"Expected extension at {extensionPath}");
    }

    [Theory]
    [InlineData("--extensions all")]
    [InlineData("--extension-locations project")]
    public async Task AgentInitCommand_ExplicitExtensionRequestFailsWhenBundleResolutionFails(string extensionOptions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire Extensions bundle is unavailable.";
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null)
            {
                ExtensionResult = AspireSkillsInstallResult.Failed(installFailureMessage)
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            $"--skill-locations none --skills none {extensionOptions}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Contains(installFailureMessage, interactionService.DisplayedErrors);
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) &&
                message.Message == AgentCommandStrings.ConfigurationCompletedWithErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SkipOnPlatform(TestPlatforms.Windows, "Creating symbolic links on Windows requires elevated privileges or Developer Mode.")]
    public async Task AgentInitCommand_DoesNotFollowDestinationReparsePoints(bool linkDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outsideDirectory = workspace.CreateDirectory("outside");
        var extensionBaseDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions");
        string? outsideFile = null;
        if (linkDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(extensionBaseDirectory)!);
            Directory.CreateSymbolicLink(extensionBaseDirectory, outsideDirectory.FullName);
        }
        else
        {
            var extensionDirectory = Directory.CreateDirectory(Path.Combine(extensionBaseDirectory, "aspire-doctor"));
            outsideFile = Path.Combine(outsideDirectory.FullName, "outside.mjs");
            await File.WriteAllTextAsync(outsideFile, "outside", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(Path.Combine(extensionDirectory.FullName, "extension.mjs"), outsideFile);
        }

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        if (outsideFile is not null)
        {
            Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.Empty(outsideDirectory.EnumerateFileSystemInfos());
        }
    }

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read)]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Windows file sharing semantics are required.")]
    public async Task AgentInitCommand_DoesNotPartiallyUpdateAssetWhenFileOperationFails(FileShare fileShare)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var extensionDirectory = workspace.CreateDirectory(Path.Combine(".github", "extensions", "aspire-doctor"));
        var extensionPath = Path.Combine(extensionDirectory.FullName, "extension.mjs");
        var binaryDirectory = extensionDirectory.CreateSubdirectory("ui");
        var binaryPath = Path.Combine(binaryDirectory.FullName, "icon.bin");
        await File.WriteAllTextAsync(extensionPath, "original extension", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(binaryPath, [0x01, 0x02, 0x03], TestContext.Current.CancellationToken);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                result: null);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations none --skills none --extension-locations project --extensions aspire-doctor");

        int exitCode;
        // FileShare.None fails while staging the second file. FileShare.Read permits staging but
        // prevents the later rename, exercising rollback after the first file was published.
        await using (File.Open(binaryPath, FileMode.Open, FileAccess.Read, fileShare))
        {
            exitCode = await result.InvokeAsync().DefaultTimeout();
        }

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal("original extension", await File.ReadAllTextAsync(extensionPath, TestContext.Current.CancellationToken));
        var binaryContent = await File.ReadAllBytesAsync(binaryPath, TestContext.Current.CancellationToken);
        Assert.Equal<byte>([0x01, 0x02, 0x03], binaryContent);
    }

    [Theory]
    [InlineData("agent init --extensions none --extension-locations none")]
    [InlineData("init --extensions none --extension-locations none")]
    [InlineData("new --extensions none --extension-locations none")]
    public void ExtensionOptions_AreAvailableOnAgentInitInitAndNew(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(commandLine);

        Assert.Empty(result.Errors);
        Assert.Equal("none", result.GetValue(AgentInitCommand.s_extensionsOption));
        Assert.Equal("none", result.GetValue(AgentInitCommand.s_extensionLocationsOption));
    }

    [Theory]
    [InlineData("--skill-locations project --skills aspire --extension-locations none --extensions none")]
    [InlineData("--skill-locations none --skills none --extension-locations standard --extensions aspire-doctor")]
    public async Task AgentInitCommand_DoesNotAcceptTargetsFromAnotherAssetKind(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} {options}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.MissingRequiredArgument, exitCode);
    }

    [Fact]
    public async Task AgentInitCommand_SkillsOnlySelectionRemainsIndependentFromExtensions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} " +
            "--skill-locations standard --skills aspire --extension-locations none --extensions none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertSkillFileExists(
            workspace.WorkspaceRoot,
            Path.Combine(".agents", "skills"),
            CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions")));
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
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);

        var expectedSummary = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(
                CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                $"{CommonAgentApplicators.AspireSkillName}, {CommonAgentApplicators.AspireDeploymentSkillName}, {FakeAspireSkillsInstaller.AspireInitSkillName}, {FakeAspireSkillsInstaller.AspireMonitoringSkillName}, {FakeAspireSkillsInstaller.AspireOrchestrationSkillName}")}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, ".agents/skills, ~/.agents/skills")}");
        var message = Assert.Single(
            interactionService.DisplayedMessages,
            displayedMessage => displayedMessage.Emoji.Equals(KnownEmojis.Robot) &&
                displayedMessage.Message.StartsWith(AgentCommandStrings.InitCommand_InstalledSkillsSummary, StringComparison.Ordinal));
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
                AgentAssetLocation location => location == AgentAssetLocation.Standard,
                AgentAssetDefinition asset => asset.HasName(CommonAgentApplicators.AspireSkillName),
                _ => false
            })
            .ToList();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
    public async Task AgentInitCommand_NonInteractive_WithAllLocationsAndSkills_InstallsSkillFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills all");

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
            FakeAspireSkillsInstaller.AspireOrchestrationSkillName
        };
        var expectedSkillDirectories = new[]
        {
            Path.Combine(".agents", "skills"),
            Path.Combine(".claude", "skills"),
            Path.Combine(".github", "skills"),
            Path.Combine(".opencode", "skill")
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
            if (items.FirstOrDefault() is AgentAssetLocation location)
            {
                return location.AssetKind is AgentAssetKind.Skill ? [AgentAssetLocation.Standard] : [];
            }

            promptedSkillNames.AddRange(items.OfType<AgentFileAssetDefinition>().Select(static skill => skill.Name));
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
            if (items.FirstOrDefault() is AgentAssetLocation location)
            {
                return location.AssetKind is AgentAssetKind.Skill ? [AgentAssetLocation.Standard] : [];
            }

            var skill = Assert.Single(items.OfType<AgentFileAssetDefinition>(), static skill => skill.HasName(FakeAspireSkillsInstaller.AspireMonitoringSkillName));
            formattedSkill = formatter(skill);
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
            if (items.FirstOrDefault() is AgentAssetLocation location)
            {
                return location.AssetKind is AgentAssetKind.Skill ? [AgentAssetLocation.Standard] : [];
            }

            var skill = Assert.Single(items.OfType<AgentFileAssetDefinition>(), static skill => skill.HasName(FakeAspireSkillsInstaller.AspireMonitoringSkillName));
            formattedSkill = formatter(skill);
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
            if (items.FirstOrDefault() is AgentAssetLocation location)
            {
                return location.AssetKind is AgentAssetKind.Skill ? [AgentAssetLocation.Standard] : [];
            }

            promptedSkillNames.AddRange(items.OfType<AgentFileAssetDefinition>().Select(static skill => skill.Name));
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills {FakeAspireSkillsInstaller.AspireMonitoringSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".claude", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".github", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".opencode", "skill"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithCliDefinedSkillDifferentCasing_DoesNotResolveBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire Skills bundle is unavailable.";
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills PLAYWRIGHT-CLI");

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
            (AgentAssetCatalog.PlaywrightCli.Name, "Bundle-provided Playwright collision"));
        var promptedSkills = new List<AgentFileAssetDefinition>();
        var interactionService = new TestInteractionService();
        interactionService.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();
            if (items.FirstOrDefault() is AgentAssetLocation location)
            {
                return location.AssetKind is AgentAssetKind.Skill ? [AgentAssetLocation.Standard] : [];
            }

            promptedSkills.AddRange(items.OfType<AgentFileAssetDefinition>());
            return [];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AspireSkillsInstallerFactory = serviceProvider => new FakeAspireSkillsInstaller(
                serviceProvider.GetRequiredService<CliExecutionContext>(),
                AspireSkillsInstallResult.Installed(bundle));
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var playwrightSkill = Assert.Single(promptedSkills, static skill => skill.HasName(AgentAssetCatalog.PlaywrightCli.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Same(AgentAssetCatalog.PlaywrightCli, playwrightSkill);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneLocations_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No locations selected, so no skill directories should be created
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents");
        Assert.False(Directory.Exists(agentsDir), $"Expected no .agents directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutSkillLocations_UsesDefaultLocations()
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Default Aspire skills are installed (all bundle skills except the one-time setup asset).
        // Aspireify is filtered out by ExcludeOneTimeSetupAssetsFromDefaults; Playwright is
        // a CLI-defined skill that is not default.
        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireDeploymentSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireInitSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        var aspireifySkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireifySkillName);
        Assert.False(Directory.Exists(aspireifySkillPath), $"Expected no aspireify skill directory but found {aspireifySkillPath}");
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
        var installResult = await installer.InstallAsync(
            AgentAssetKind.Skill,
            TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotNull(installResult.Bundle);
        var bundleSkillNames = installResult.Bundle.GetAssetDefinitions().Select(static asset => asset.Name).ToList();
        Assert.NotEmpty(bundleSkillNames);

        // Explicit names instead of `all` keeps the assertion focused on bundle skills and
        // avoids dragging in Playwright/dotnet-inspect, which would attempt real network calls.
        var skillsArg = string.Join(",", bundleSkillNames);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills {skillsArg}");

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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills {FakeAspireSkillsInstaller.AspireOrchestrationSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected only the selected skill but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidSkillLocations_FailsWithMissingArgument()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations invalid --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.MissingRequiredArgument, exitCode);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutWorkspaceRoot_UsesWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireDeploymentSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireInitSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireMonitoringSkillName);
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), FakeAspireSkillsInstaller.AspireOrchestrationSkillName);
        var aspireifySkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireifySkillName);
        Assert.False(Directory.Exists(aspireifySkillPath), $"Expected no aspireify skill directory but found {aspireifySkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithUnavailableAspireSkillsBundle_SucceedsWithoutWarningOrSelectedAspireSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire Skills bundle is unavailable.";
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
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.DoesNotContain(installFailureMessage, interactionService.DisplayedErrors);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) && message.Message == installFailureMessage);
        Assert.Contains(McpCommandStrings.InitCommand_ConfigurationComplete, interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task PromptAndChainAsync_WithUnavailableAspireSkillsBundle_SucceedsWithoutWarningOrSelectedAspireSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string installFailureMessage = "Aspire Skills bundle is unavailable.";
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
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
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(
            result.GetAssets(AgentAssetKind.Skill).OfType<AgentFileAssetDefinition>(),
            static skill => skill.SourceKind is AgentFileAssetSourceKind.AspireSkillsBundle);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => message.Emoji.Equals(KnownEmojis.Warning) && message.Message == installFailureMessage);
        Assert.Contains(McpCommandStrings.InitCommand_ConfigurationComplete, interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task PromptAndChainAsync_WithoutPredicateOverride_PreSelectsBundleDefaultsIncludingAspireify()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AgentInitCommand>();

        // Passing no predicate pre-selects every bundle-sourced skill, which is the semantic
        // `aspire init` relies on so the one-time wiring skill chains into the flow.
        var result = await command.PromptAndChainAsync(
            interactionService,
            CliExitCodes.Success,
            workspace.WorkspaceRoot,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            null,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(
            result.GetAssets(AgentAssetKind.Skill),
            static skill => skill.HasName(CommonAgentApplicators.AspireifySkillName));
        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireifySkillName);
    }

    [Fact]
    public async Task PromptAndChainAsync_WithExcludeAspireifyPredicate_DoesNotPreSelectAspireify()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<AgentInitCommand>();

        // Callers that just created a new AppHost (aspire new) or are running standalone agent
        // init pass a predicate that strips aspireify from every asset-kind default selection.
        // The assets remain in their prompts — they are just not pre-checked.
        var result = await command.PromptAndChainAsync(
            interactionService,
            CliExitCodes.Success,
            workspace.WorkspaceRoot,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            PromptBinding.CreateDefault<string?>(null),
            AgentInitCommand.ExcludeOneTimeSetupAssetsFromDefaults,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(
            result.GetAssets(AgentAssetKind.Skill),
            static skill => skill.HasName(CommonAgentApplicators.AspireifySkillName));
        var aspireifySkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireifySkillName);
        Assert.False(Directory.Exists(aspireifySkillPath), $"Expected no aspireify skill directory but found {aspireifySkillPath}");
    }

    [Fact]
    public void ExcludeOneTimeSetupAssetsFromDefaults_ExcludesAspireifyAcrossAssetKinds()
    {
        // Only file-backed kinds can come from the skills bundle; Mcp is action-backed by
        // construction, so it can never carry a bundle-sourced aspireify asset.
        foreach (var assetKind in new[] { AgentAssetKind.Skill, AgentAssetKind.Extension })
        {
            var aspireify = AgentFileAssetDefinition.CreateAspireSkillsBundle(
                assetKind,
                CommonAgentApplicators.AspireifySkillName,
                "One-time Aspire workspace setup");

            Assert.False(AgentInitCommand.ExcludeOneTimeSetupAssetsFromDefaults(aspireify));
        }
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneSkills_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No skills selected, so no skill files should be created
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected no aspire skill directory but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_McpAssetDefaultsToUnselected()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applyCount = 0;
        var detector = new FakeAgentEnvironmentDetector(AgentClientKind.VsCode)
        {
            Applicators =
            [
                AgentEnvironmentApplicator.ForAsset(
                    AgentAssetCatalog.AspireMcpServer,
                    "vscode",
                    "VS Code MCP",
                    _ =>
                    {
                        applyCount++;
                        return Task.CompletedTask;
                    })
            ]
        };
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // The MCP server is opt-in, so a non-interactive run that never selects it must leave the
        // detected agent environments untouched.
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(0, applyCount);
    }

    [Theory]
    [InlineData("aspire")]
    [InlineData("all")]
    public async Task AgentInitCommand_NonInteractive_WithMcpsSelection_AppliesDetectedMcpActions(string mcps)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applyCount = 0;
        var detector = new FakeAgentEnvironmentDetector(AgentClientKind.VsCode)
        {
            Applicators =
            [
                AgentEnvironmentApplicator.ForAsset(
                    AgentAssetCatalog.AspireMcpServer,
                    "vscode",
                    "VS Code MCP",
                    _ =>
                    {
                        applyCount++;
                        return Task.CompletedTask;
                    })
            ]
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => detector;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills none --mcps {mcps}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, applyCount);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpsAspire_WithoutDetectedClientFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) =>
            {
                var items = choices.Cast<object>().ToList();
                return items.FirstOrDefault() is AgentAssetLocation ? [] : items;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills none --mcps aspire");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Contains(
            AgentCommandStrings.InitCommand_NoCompatibleClientForSelectedMcp,
            interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithSkillLocationsNone_DoesNotInstallAnySkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // With --skill-locations none, no skill files should be installed even when --skills all is passed.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills");
        Assert.False(Directory.Exists(agentsDir), $"Expected no agents/skills directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithSkillLocationsAndSkills_InstallsOnlySpecifiedSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations standard --skills {CommonAgentApplicators.AspireSkillName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        AssertSkillFileExists(workspace.WorkspaceRoot, Path.Combine(".agents", "skills"), CommonAgentApplicators.AspireSkillName);

        // aspireify was not requested, so it should not be installed.
        var aspireifySkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireifySkillName);
        Assert.False(Directory.Exists(aspireifySkillPath), $"Expected no aspireify skill directory but found {aspireifySkillPath}");
    }

    private async Task AssertDetectedClientDefaultSkillLocationAsync(
        AgentClientKind client,
        string expectedSkillsDirectory,
        string unexpectedParentDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(client);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skills {CommonAgentApplicators.AspireSkillName} --mcps none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertSkillFileExists(
            workspace.WorkspaceRoot,
            expectedSkillsDirectory,
            CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, unexpectedParentDirectory)));
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

        var manifestSkills = new List<SkillBundleAsset>();
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

            manifestSkills.Add(new SkillBundleAsset
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
            Assets = [.. manifestSkills]
        };

        var provider = new SkillBundleProvider();
        var manifestJson = JsonSerializer.Serialize(manifest, provider.CreateManifestTypeInfo());
        var manifestPath = Path.Combine(bundleDirectory.FullName, "skill-manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifestJson);
        return await provider.LoadAsync(bundleDirectory, CancellationToken.None);
    }

    private static string ComputeSha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
    }

    [Theory]
    [InlineData(nameof(AgentClientKind.CopilotCli), "GitHub Copilot CLI")]
    [InlineData(nameof(AgentClientKind.CopilotApp), "GitHub Copilot App")]
    public async Task AgentInitCommand_DefaultOn_InstallsTelemetryHook_ForDetectedClient(string clientKind, string displayName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        var client = Enum.Parse<AgentClientKind>(clientKind);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(client);
            options.InteractionServiceFactory = _ => interactionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills none");

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
            options.AgentEnvironmentDetectorFactory = _ => new FakeAgentEnvironmentDetector(AgentClientKind.CopilotCli);
            options.InteractionServiceFactory = _ => interactionService;
            options.TelemetryHookConfiguratorFactory = _ => new ThrowingTelemetryHookConfigurator(failureMessage);
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skill-locations none --skills none");

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

    /// <summary>
    /// A configurator that always throws, simulating a non-IO failure (e.g. a missing embedded hook
    /// script surfacing as <see cref="InvalidOperationException"/>) so the best-effort catch in
    /// <c>agent init</c> can be verified to never abort the command.
    /// </summary>
    private sealed class ThrowingTelemetryHookConfigurator(string message) : ITelemetryHookConfigurator
    {
        public Task<TelemetryHookConfigurationResult> ConfigureAsync(
            IReadOnlyCollection<AgentClientKind> detectedClients,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }
}
