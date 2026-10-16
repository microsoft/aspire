// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitSkillsModeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("agent init")]
    [InlineData("mcp init")]
    [InlineData("new aspire-empty")]
    [InlineData("init")]
    public void SourceMode_IsNamedExclusiveAndDefaultsToNative(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, output).BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        var defaults = root.Parse(command);
        Assert.Empty(defaults.Errors);
        Assert.Equal(AspireSkillsMode.Native, defaults.GetValue<AspireSkillsMode>("--aspire-skills"));
        foreach (var mode in new[] { AspireSkillsMode.Native, AspireSkillsMode.None })
        {
            var result = root.Parse($"{command} --aspire-skills {mode.ToString().ToUpperInvariant()}");
            Assert.Empty(result.Errors);
            Assert.Equal(mode, result.GetValue<AspireSkillsMode>("--aspire-skills"));
        }
        foreach (var options in new[] { "--aspire-skills", "--aspire-skills true", "--aspire-skills all", "--aspire-skills bundled", "--aspire-skills 0", "--aspire-skills native,none", "--skills aspire" })
        {
            Assert.NotEmpty(root.Parse($"{command} {options}").Errors);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" y", true)]
    [InlineData("=TRUE", true)]
    [InlineData(" n", false)]
    [InlineData("=false", false)]
    public void IndependentAssetFlags_AcceptBareOrExplicitConfirmation(string suffix, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, output).BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        foreach (var option in new[] { "--mcp", "--playwright", "--dotnet-inspect" })
        {
            var result = root.Parse($"agent init {option}{suffix}");
            Assert.Empty(result.Errors);
            Assert.Equal(expected ? AgentConfirmation.Yes : AgentConfirmation.No, result.GetValue<AgentConfirmation?>(option));
            Assert.NotEmpty(root.Parse($"agent init {option} invalid").Errors);
        }
    }

    [Theory]
    [InlineData("copilot", "project", ".github/copilot/settings.json")]
    [InlineData("copilot", "global", ".copilot/settings.json")]
    [InlineData("claude", "project", ".claude/settings.json")]
    [InlineData("claude", "global", ".claude/settings.json")]
    [InlineData("opencode", "project", "opencode.jsonc")]
    [InlineData("opencode", "global", ".config/opencode/opencode.jsonc")]
    public async Task Native_RegistersSelectedUndetectedClientsWithoutMcp(string client, string scope, string relativePath)
    {
        using var context = new AgentConfigurationTestContext(output);
        var messages = new List<string>();
        var interaction = new TestInteractionService { DisplaySubtleMessageCallback = messages.Add };
        var runner = new FakeOpenCodeCliRunner(null);
        var services = CliTestHelper.CreateServiceCollection(context.Workspace, output, options =>
        {
            options.CliExecutionContextFactory = _ => context.ExecutionContext;
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(context.Environment);
        services.AddSingleton<IOpenCodeCliRunner>(runner);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {client} --scope {scope} --aspire-skills native --mcp n --playwright n --dotnet-inspect n");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var root = scope == "project" ? context.Project : context.Home;
        var otherRoot = scope == "project" ? context.Home : context.Project;
        var expectedPath = Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal([expectedPath], Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories));
        Assert.Empty(otherRoot.EnumerateFileSystemInfos());
        var config = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath))!.AsObject();
        Assert.Equal(client == "opencode" ? ["skills"] : new[] { "enabledPlugins", "extraKnownMarketplaces" }, config.Select(property => property.Key).Order());
        Assert.Equal(0, runner.Calls);
        Assert.Contains(AgentCommandStrings.InitCommand_ClientAcquisitionNotice, messages);
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Fact]
    public async Task LocalCopiedSkills_StopBeforeIndependentAssetsOrHooks()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        const string content = "My customized Aspire instructions.";
        await AgentConfigurationTestContext.WriteAsync(path, content);
        var hooks = new TestTelemetryHookConfigurator();
        var services = CliTestHelper.CreateServiceCollection(context.Workspace, output, options =>
        {
            options.CliExecutionContextFactory = _ => context.ExecutionContext;
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        services.AddSingleton<IEnvironment>(context.Environment);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var before = Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = command.Parse(
            "agent init --agents copilot --scope global --aspire-skills native --mcp --playwright --dotnet-inspect");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(before, Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(AgentEnvironment.None, hooks.ConfiguredEnvironments);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Native_FailedRegistration_StillConfiguresIndependentlyRequestedMcp(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        var client = context.Clients.Single(client => client.Id == clientId);
        var nativePath = client.CreateNativeSkillsEdit(context.Project, AgentConfigurationScope.Project).TargetPath;
        const string malformed = "{broken";
        await AgentConfigurationTestContext.WriteAsync(nativePath, malformed);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(context.Workspace, output, options =>
        {
            options.CliExecutionContextFactory = _ => context.ExecutionContext;
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(context.Environment);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clientId} --scope project --aspire-skills native --mcp");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Single(interaction.DisplayedErrors);
        Assert.Equal(malformed, await File.ReadAllTextAsync(nativePath));
        var mcpPath = Path.Combine(context.Project.FullName, ".mcp.json");
        var server = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!["mcpServers"]!["aspire"]!;
        Assert.Equal("aspire", (string?)server["command"]);
        Assert.Equal(["agent", "mcp"], server["args"]!.AsArray().Select(value => (string?)value));
    }
}
