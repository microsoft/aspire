// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoClients_SucceedsWithoutConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".claude")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutClientSelection_UsesDefaultClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --aspire-skills none");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidClient_FailsValidation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents invalid");

        Assert.NotEqual(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneSkills_SucceedsWithoutSkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills none");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_DoesNotConfigureMcpByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills none");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpTrue_ConfiguresMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpFalse_DoesNotConfigureMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --aspire-skills none --mcp=false");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
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
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var hookFile = Path.Combine(homeDirectory.FullName, ".copilot", "hooks", "aspire-telemetry.json");
        Assert.True(File.Exists(hookFile), $"Expected telemetry hook at {hookFile}");
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Message.Contains(displayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_DoesNotFail_WhenTelemetryHookConfigurationThrows()
    {
        // Hook installation is best-effort transparency tooling. A failure must not abort agent init.
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
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Contains(failureMessage, subtleMessages);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo homeDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: homeDirectory);
    }
}
