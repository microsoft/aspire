// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentExtensionInstallationTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InteractivePrompts_KeepSkillsAndExtensionsSeparate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var prompts = new List<string>();
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, _, _) =>
            {
                prompts.Add(prompt);
                var expectedKind = prompt == AgentCommandStrings.InitCommand_SelectSkillLocations ||
                    prompt == AgentCommandStrings.InitCommand_SelectSkills ? AgentAssetKind.Skill : AgentAssetKind.Extension;
                return choices.Cast<object>().Where(choice =>
                {
                    switch (choice)
                    {
                        case AgentAssetLocation location:
                            Assert.Equal(expectedKind, location.AssetKind);
                            return location.IsDefault;
                        case AgentAssetDefinition asset:
                            Assert.Equal(expectedKind, asset.AssetKind);
                            return asset.IsDefault;
                        default:
                            throw new InvalidOperationException($"Unexpected choice: {choice}");
                    }
                }).ToList();
            }
        };
        interaction.SetupStringPromptResponse(workspace.WorkspaceRoot.FullName);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedClients = [AgentClientKind.CopilotApp] };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal([
            AgentCommandStrings.InitCommand_SelectSkillLocations,
            AgentCommandStrings.InitCommand_SelectSkills,
            AgentCommandStrings.InitCommand_SelectExtensionLocations,
            AgentCommandStrings.InitCommand_SelectExtensions
        ], prompts);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspireify", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor", "extension.mjs")));
    }

    [Theory]
    [InlineData("project", true, false)]
    [InlineData("user", false, true)]
    [InlineData("all", true, true)]
    public async Task ExplicitExtensions_InstallOnlyInSelectedLocations(string location, bool project, bool user)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: home);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse($"agent init --skill-locations none --extension-locations {location} --extensions aspire-doctor")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor");
        var userDirectory = Path.Combine(home.FullName, ".copilot", "extensions", "aspire-doctor");
        Assert.Equal(project, Directory.Exists(projectDirectory));
        Assert.Equal(user, Directory.Exists(userDirectory));
        foreach (var directory in new[] { projectDirectory, userDirectory }.Where(Directory.Exists))
        {
            Assert.Equal("export default {};", await File.ReadAllTextAsync(Path.Combine(directory, "extension.mjs")));
            Assert.Equal(new byte[] { 0x00, 0xff, 0x80, 0x0a }, await File.ReadAllBytesAsync(Path.Combine(directory, "ui", "icon.bin")));
        }
        Assert.Equal([AgentAssetKind.Extension], Assert.IsType<FakeAspireSkillsInstaller>(provider.GetRequiredService<IAspireSkillsInstaller>()).RequestedAssetKinds);
    }

    [Theory]
    [InlineData(nameof(AgentClientKind.CopilotApp), true)]
    [InlineData(nameof(AgentClientKind.CopilotCli), false)]
    [InlineData(nameof(AgentClientKind.VsCode), false)]
    [InlineData(nameof(AgentClientKind.ClaudeCode), false)]
    [InlineData(nameof(AgentClientKind.OpenCode), false)]
    public async Task DefaultExtensions_AreOnlyOfferedToSupportedClient(string clientName, bool installed)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: home);
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector
            {
                DetectedClients = [Enum.Parse<AgentClientKind>(clientName)]
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("agent init --skill-locations none").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(installed, File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor", "extension.mjs")));
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".copilot", "extensions")));
        Assert.Equal(installed ? [AgentAssetKind.Extension] : [], Assert.IsType<FakeAspireSkillsInstaller>(provider.GetRequiredService<IAspireSkillsInstaller>()).RequestedAssetKinds);
    }

    [Theory]
    [InlineData("--extension-locations none")]
    [InlineData("--extensions none")]
    public async Task ExplicitOptOut_DoesNotResolveExtensionBundle(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedClients = [AgentClientKind.CopilotApp] };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse($"agent init --skill-locations none {arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(Assert.IsType<FakeAspireSkillsInstaller>(provider.GetRequiredService<IAspireSkillsInstaller>()).RequestedAssetKinds);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions")));
    }

    [Theory]
    [InlineData("--extension-locations invalid --extensions all")]
    [InlineData("--extension-locations project --extensions invalid")]
    public async Task InvalidExtensionSelection_Fails(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse($"agent init --skill-locations none {arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.MissingRequiredArgument, exitCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions")));
    }

    [Fact]
    public async Task UserExtensions_RespectCopilotHome()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var copilotHome = workspace.CreateDirectory("custom-copilot");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: home);
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?> { ["COPILOT_HOME"] = copilotHome.FullName }));
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("agent init --skill-locations none --extension-locations user --extensions all")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(copilotHome.FullName, "extensions", "aspire-doctor", "extension.mjs")));
        Assert.False(Directory.Exists(Path.Combine(home.FullName, ".copilot", "extensions")));
    }

    [Fact]
    public async Task FailedExtensionBundle_IsReportedAndDoesNotUndoSkills()
    {
        const string failureMessage = "Extension artifact is unavailable.";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AspireSkillsInstallerFactory = sp => new FakeAspireSkillsInstaller(sp.GetRequiredService<CliExecutionContext>())
            {
                ExtensionResult = AspireSkillsInstallResult.Failed(failureMessage)
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("agent init --skills aspire --extension-locations project --extensions all")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Contains(failureMessage, interaction.DisplayedErrors);
        Assert.Contains(interaction.DisplayedMessages, message =>
            message.Message == AgentCommandStrings.InitCommand_NoCompatibleClientForExplicitExtensions);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions")));
        Assert.Empty(interaction.DisplayedSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extensions_DoNotChangeStandaloneMcpOptIn(bool configureMcp)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var applied = false;
        var applicator = new AgentEnvironmentApplicator("Configure MCP", _ =>
        {
            applied = true;
            return Task.CompletedTask;
        });
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector(applicator);
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var mcpOption = configureMcp ? " --mcp" : "";

        var exitCode = await command.Parse($"agent init --skill-locations none --extension-locations project --extensions all{mcpOption}")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(configureMcp, applied);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "extensions", "aspire-doctor", "extension.mjs")));
    }
}
