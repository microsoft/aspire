// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class AgentCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AgentCommands_AllHelpOutputs_AreCorrect()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync("aspire agent --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("mcp") && s.ContainsText("init"),
            timeout: TimeSpan.FromSeconds(30), description: "agent help showing mcp and init subcommands");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire agent mcp --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire agent mcp [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire agent init --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire agent init [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire mcp --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("tools") && s.ContainsText("call"),
            timeout: TimeSpan.FromSeconds(30), description: "mcp help showing tools and call subcommands");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire mcp tools --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire mcp tools [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    public async Task AgentInitCommand_MigratesOnlySelectedMcpConfiguration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export CLAUDE_CONFIG_DIR=\"$PWD/.client-config/claude\"", counter);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        File.WriteAllText(configPath, """
            {"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start","--verbose"],"env":{"CUSTOM":"preserved"}},"other":{"command":"other","args":[]}}}
            """);

        await auto.RunCommandAsync(
            "aspire agent init --non-interactive --workspace-root . --mcp y --playwright n --dotnet-inspect n --aspire-skills n --clients claude-code",
            counter);

        await Verify(File.ReadAllText(configPath), "json");
    }

    [Fact]
    public async Task DoctorCommand_DetectsDeprecatedAgentConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        File.WriteAllText(configPath, """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""");
        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("dev-certs") && s.ContainsText("deprecated") && s.ContainsText("aspire agent init"),
            timeout: TimeSpan.FromSeconds(60), description: "doctor output with deprecated warning and fix suggestion");
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AgentInitCommand_DefaultSelection_RegistersNativeSourceWithoutInstallingSkills()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export COPILOT_HOME=\"$PWD/.client-config/copilot\"", counter);
        Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode"));

        await auto.TypeAsync("aspire agent init --workspace-root .");
        await auto.EnterAsync();
        await AcceptDefaultAssetsAsync(auto, includeMcp: true);
        await auto.WaitUntilAsync(
            s => s.ContainsText("Select clients to configure") && s.ContainsText("[X] VS Code"),
            timeout: TimeSpan.FromSeconds(30), description: "client selection with detected VS Code preselected");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var projectSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
        var userSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".client-config", "copilot", "settings.json");
        await Verify(ReadNativeSettings(projectSettings, userSettings), "json");
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode", "mcp.json")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills")));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AgentInit_NonInteractive_RegistersSharedCopilotTargetsIdempotently()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export COPILOT_HOME=\"$PWD/.client-config/copilot\"", counter);

        var mcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existingMcp = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        File.WriteAllText(mcpPath, existingMcp);

        // Explicit clients work even when neither Copilot frontend is installed in the container.
        const string command = "aspire agent init --non-interactive --workspace-root . --clients copilot-cli,COPILOT-APP";
        await auto.RunCommandAsync(command, counter);
        var projectSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
        var userSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".client-config", "copilot", "settings.json");
        var projectTimestamp = File.GetLastWriteTimeUtc(projectSettings);
        var userTimestamp = File.GetLastWriteTimeUtc(userSettings);
        var firstSettings = ReadNativeSettings(projectSettings, userSettings);

        await auto.RunCommandAsync(command, counter);

        Assert.Equal(projectTimestamp, File.GetLastWriteTimeUtc(projectSettings));
        Assert.Equal(userTimestamp, File.GetLastWriteTimeUtc(userSettings));
        Assert.Equal(firstSettings, ReadNativeSettings(projectSettings, userSettings));
        Assert.Equal(existingMcp, File.ReadAllText(mcpPath));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills")));
        await Verify(firstSettings, "json");
    }

    [Fact]
    public async Task AgentInit_NoSelectionOrInvalidClient_LeavesExistingConfigurationUntouched()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existing = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        File.WriteAllText(configPath, existing);

        await auto.RunCommandAsync(
            "aspire agent init --non-interactive --workspace-root . --mcp n --playwright n --dotnet-inspect n --aspire-skills n --clients claude-code",
            counter);
        Assert.Equal(existing, File.ReadAllText(configPath));

        await auto.RunCommandAsync(
            "aspire agent init --non-interactive --workspace-root . --mcp --playwright --dotnet-inspect --clients none",
            counter);
        Assert.Equal(existing, File.ReadAllText(configPath));

        await auto.TypeAsync("aspire agent init --non-interactive --workspace-root . --mcp --clients unknown-client");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            snapshot => snapshot.ContainsText($"[{counter.Value} ERR:"),
            timeout: TimeSpan.FromSeconds(30),
            description: "agent init rejecting the unknown client with a nonzero exit code");
        await auto.WaitForAnyPromptAsync(counter);
        Assert.Equal(existing, File.ReadAllText(configPath));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireInit_ChainedAgentInit_RegistersSourceWithoutOfferingMcp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export COPILOT_HOME=\"$PWD/.client-config/copilot\"", counter);

        await auto.TypeAsync("aspire init --language csharp --clients vscode");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitUntilAsync(
            s => s.ContainsText("configure AI agent environments"),
            timeout: TimeSpan.FromSeconds(30), description: "agent setup confirmation after aspire init");
        await auto.EnterAsync();

        // Waiting for Playwright first also guards the standalone-only MCP prompt boundary.
        await AcceptDefaultAssetsAsync(auto, includeMcp: false);
        await auto.WaitUntilAsync(
            s => s.ContainsText("After the client acquires") && s.ContainsText("aspireify"),
            timeout: TimeSpan.FromSeconds(30), description: "Aspireify handoff explaining native client acquisition");
        await auto.WaitForSuccessPromptAsync(counter);

        var projectSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "copilot", "settings.json");
        var userSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".client-config", "copilot", "settings.json");
        await Verify(ReadNativeSettings(projectSettings, userSettings), "json");
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode", "mcp.json")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills")));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireNew_ChainedAgentInit_RegistersAtOutputRootWithoutOfferingMcp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAgentInitContract(strategy);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export COPILOT_HOME=\"$PWD/.client-config/copilot\"", counter);
        await auto.AspireNewAcceptingAgentInitAsync("StarterApp", extraArguments: "--clients vscode");
        await AcceptDefaultAssetsAsync(auto, includeMcp: false);
        await auto.WaitForSuccessPromptAsync(counter);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "StarterApp");
        var projectSettings = Path.Combine(projectRoot, ".github", "copilot", "settings.json");
        var userSettings = Path.Combine(workspace.WorkspaceRoot.FullName, ".client-config", "copilot", "settings.json");
        await Verify(ReadNativeSettings(projectSettings, userSettings), "json");
        Assert.False(File.Exists(Path.Combine(projectRoot, ".vscode", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "copilot", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".agents", "skills")));
    }

    private static async Task AcceptDefaultAssetsAsync(Hex1bTerminalAutomator auto, bool includeMcp)
    {
        if (includeMcp)
        {
            await auto.WaitUntilAsync(
                s => s.ContainsText("Configure the Aspire MCP server for the selected clients?"),
                timeout: TimeSpan.FromSeconds(30), description: "MCP asset prompt, default No");
            await auto.EnterAsync();
        }

        await auto.WaitUntilAsync(
            s => s.ContainsText("Install Playwright CLI and its browser automation skill?"),
            timeout: TimeSpan.FromSeconds(30), description: "Playwright asset prompt, default No");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Install the dotnet-inspect bootstrap skill?"),
            timeout: TimeSpan.FromSeconds(30), description: "dotnet-inspect asset prompt, default No");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Register Aspire skills and supported canvases?"),
            timeout: TimeSpan.FromSeconds(30), description: "native Aspire asset prompt, default Yes");
        await auto.EnterAsync();
    }

    private static string ReadNativeSettings(string projectSettings, string userSettings)
    {
        var settings = new JsonObject
        {
            ["project"] = JsonNode.Parse(File.ReadAllText(projectSettings)),
            ["user"] = JsonNode.Parse(File.ReadAllText(userSettings))
        };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RequireCurrentAgentInitContract(CliInstallStrategy strategy)
    {
        Assert.SkipWhen(
            strategy.Mode == CliInstallMode.InstallScript ||
            (strategy.Mode == CliInstallMode.DotnetTool && strategy.NupkgSourcePath is null),
            "This test validates the current CLI's independent asset and native registration contract. Use a local or PR CLI build instead of a released CLI.");
    }
}
