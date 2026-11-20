// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class LegacyTelemetryHookCheckTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("copilot", false)]
    [InlineData("copilot", true)]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    public async Task CheckAsync_LegacyHookWarnsUntilAgentInitRefreshesIt(string clientId, bool customConfigDirectory)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        if (customConfigDirectory)
        {
            context.SetVariable("COPILOT_HOME", "~/custom-copilot");
            context.SetVariable("CLAUDE_CONFIG_DIR", "~/custom-claude");
        }

        var configPath = clientId == CopilotAgentClient.ClientId
            ? Path.Combine(context.CopilotDirectory, "hooks", "aspire-telemetry.json")
            : Path.Combine(context.ClaudeDirectory, "settings.json");
        var config = clientId == CopilotAgentClient.ClientId
            ? """{"version":1,"hooks":{"postToolUse":[{"type":"command","bash":"bash '/home/user/.aspire/hooks/track-telemetry.sh'","powershell":"pwsh -File 'C:\\Users\\user\\.aspire\\hooks\\track-telemetry.ps1'"}]}}"""
            : """{"hooks":{"PostToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"bash","args":["/home/user/.aspire/hooks/track-telemetry.sh"]},{"type":"command","command":"pwsh","args":["-File","C:\\Users\\user\\.aspire\\hooks\\track-telemetry.ps1"]}]}]}}""";
        await AgentConfigurationTestContext.WriteAsync(configPath, config);
        // No MCP or skills setup exists at the selected scope. The legacy registration alone must be
        // enough for the doctor's suggested command to replace it.
        var check = CreateCheck(context);

        var warning = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, warning.Status);
        Assert.Equal(configPath, warning.Details);
        Assert.Contains($"aspire agent init --agents {clientId}", warning.Fix);
        Assert.Equal(config, await File.ReadAllTextAsync(configPath));

        var services = context.CreateServiceCollection(outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var exitCode = await command.Parse(
            $"agent init --agents {clientId} --scope project --skills none --mcp=false --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("claude", """{"hooks":{"PostToolUse":[{"hooks":[{"command":"bash '/home/user/.aspire/hooks/track-telemetry.sh'"}]}]}}""")]
    [InlineData("claude", """{"hooks":{"PostToolUse":[{"hooks":[{"command":"pwsh -File 'C:\\Users\\user\\.aspire\\hooks\\track-telemetry.ps1'"}]}]}}""")]
    [InlineData("copilot", """{"hooks":{"postToolUse":[{"powershell":"pwsh -File 'C:\\Users\\user\\.aspire\\hooks\\track-telemetry.ps1'"}]}}""")]
    public async Task CheckAsync_RecognizesLegacyHookVariants(string clientId, string config)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var configPath = clientId == CopilotAgentClient.ClientId
            ? Path.Combine(context.CopilotDirectory, "hooks", "aspire-telemetry.json")
            : Path.Combine(context.ClaudeDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(configPath, config);

        var warning = Assert.Single(await CreateCheck(context).CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, warning.Status);
        Assert.Contains($"aspire agent init --agents {clientId}", warning.Fix);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{malformed")]
    [InlineData("""{"hooks":[]}""")]
    [InlineData("""{"hooks":{"postToolUse":false,"PostToolUse":[null,{"hooks":false}]}}""")]
    [InlineData("""{"hooks":{"postToolUse":[{"exec":"aspire","args":["agent","telemetry","--hook"]}],"PostToolUse":[{"hooks":[{"command":"aspire","args":["agent","telemetry","--hook"]}]}]}}""")]
    [InlineData("""{"hooks":{"postToolUse":[{"bash":"bash '/custom/track-telemetry.sh'"}],"PostToolUse":[{"hooks":[{"command":"bash","args":["/custom/track-telemetry.sh"]}]}]}}""")]
    [InlineData("""{"hooks":{"postToolUse":[{"bash":"bash '/home/user/.aspire/hooks/track-telemetry.sh.backup'"}],"PostToolUse":[{"hooks":[{"command":"pwsh","args":["-File","C:\\Users\\user\\.aspire\\hooks\\track-telemetry.ps1.old"]}]}]}}""")]
    public async Task CheckAsync_DoesNotWarnWithoutLegacyRegistrations(string config)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(context.CopilotDirectory, "hooks", "aspire-telemetry.json"), config);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.ClaudeDirectory, "settings.json"), config);

        Assert.Empty(await CreateCheck(context).CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("""{"hooks":{},"hooks":{}}""")]
    public async Task CheckAsync_UnreadableCopilotConfigurationDoesNotHideClaudeWarning(string copilotConfig)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(context.CopilotDirectory, "hooks", "aspire-telemetry.json"), copilotConfig);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.ClaudeDirectory, "settings.json"),
            """{"hooks":{"PostToolUse":[{"hooks":[{"command":"bash","args":["/home/user/.aspire/hooks/track-telemetry.sh"]}]}]}}""");

        var warning = Assert.Single(await CreateCheck(context).CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(LegacyTelemetryHookCheck.GetCheckName("claude"), warning.Name);
    }

    [Fact]
    public async Task CheckAsync_LeftoverScriptFilesWithoutRegistrationsDoNotWarn()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        foreach (var script in new[] { "track-telemetry.sh", "track-telemetry.ps1" })
        {
            await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Home.FullName, ".aspire", "hooks", script), "legacy script");
        }

        Assert.Empty(await CreateCheck(context).CheckAsync(TestContext.Current.CancellationToken));
    }

    private static LegacyTelemetryHookCheck CreateCheck(AgentConfigurationTestContext context)
        => new(context.Clients, NullLogger<LegacyTelemetryHookCheck>.Instance);
}
