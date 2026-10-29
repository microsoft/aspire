// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI agent commands, testing the new `aspire agent`
/// command structure and backward compatibility with `aspire mcp` commands.
/// </summary>
public sealed class AgentCommandTests(ITestOutputHelper output)
{
    /// <summary>
    /// Tests that all agent command help outputs are correct, including:
    /// - aspire agent --help (shows subcommands: mcp, init)
    /// - aspire agent mcp --help (shows MCP server description)
    /// - aspire agent init --help (shows init description)
    /// - aspire mcp --help (legacy, still works)
    /// - aspire mcp start --help (legacy, still works)
    /// </summary>
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

        // Test 1: aspire agent --help
        await auto.TypeAsync("aspire agent --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("mcp") && s.ContainsText("init"),
            timeout: TimeSpan.FromSeconds(30), description: "agent help showing mcp and init subcommands");
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 2: aspire agent mcp --help
        await auto.TypeAsync("aspire agent mcp --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire agent mcp [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 3: aspire agent init --help
        await auto.TypeAsync("aspire agent init --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire agent init [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilAsync(
            s => s.ContainsText("--agents") && s.ContainsText("--scope") && s.ContainsText("--skills") &&
                s.ContainsText("--aspire-source") && s.ContainsText("--mcp"),
            timeout: TimeSpan.FromSeconds(30), description: "agent init help showing clients, scope, skills, source, and separate MCP");
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 4: aspire mcp --help (now shows tools and call subcommands)
        await auto.TypeAsync("aspire mcp --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("tools") && s.ContainsText("call"),
            timeout: TimeSpan.FromSeconds(30), description: "mcp help showing tools and call subcommands");
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 5: aspire mcp tools --help
        await auto.TypeAsync("aspire mcp tools --help");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("aspire mcp tools [options]", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Tests that deprecated MCP configs are detected and can be migrated
    /// to the new agent mcp format during aspire agent init.
    /// </summary>
    [Fact]
    public async Task AgentInitCommand_MigratesDeprecatedConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        // Use .mcp.json (Claude Code format) for simpler testing
        // This is the same format used by the doctor test that passes
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        var containerConfigPath = CliE2ETestHelpers.ToContainerPath(configPath, workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create deprecated config file using Claude Code format (.mcp.json)
        // This simulates a config that was created by an older version of the CLI
        // Using single-line JSON to avoid any whitespace parsing issues
        File.WriteAllText(configPath, """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""");

        // Verify the deprecated config was created
        var fileContent = File.ReadAllText(configPath);
        Assert.Contains("\"mcp\"", fileContent);
        Assert.Contains("\"start\"", fileContent);

        // Debug: Show that the file exists and where we are
        await auto.TypeAsync($"ls -la {containerConfigPath} && pwd");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(".mcp.json", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Explicit standalone MCP setup repairs the selected client's deprecated config.
        // Skill installation is not part of this migration coverage, so keep it disabled
        // to avoid depending on the external Aspire skills package.
        await auto.TypeAsync("aspire agent init --workspace-root . --agents claude --scope project --skills none --mcp");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Verify config was updated to new format
        // The updated config should contain "agent" and "mcp" but not "start"
        fileContent = File.ReadAllText(configPath);
        Assert.Contains("\"agent\"", fileContent);
        Assert.Contains("\"mcp\"", fileContent);
        Assert.DoesNotContain("\"start\"", fileContent);
    }

    /// <summary>
    /// Tests that aspire doctor warns about deprecated agent configs.
    /// </summary>
    [Fact]
    public async Task DoctorCommand_DetectsDeprecatedAgentConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create deprecated config file
        File.WriteAllText(configPath, """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""");
        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("dev-certs") && s.ContainsText("deprecated") && s.ContainsText("aspire agent init"),
            timeout: TimeSpan.FromSeconds(60), description: "doctor output with deprecated warning and fix suggestion");
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Tests that aspire agent init with a .vscode folder shows client, skill, and scope selection
    /// prompts, and that accepting the defaults registers the remote Aspire plugin.
    /// </summary>
    [Fact]
    public async Task AgentInitCommand_DefaultSelection_RegistersRemotePlugin()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        // Set up .vscode folder so VS Code scanner detects it
        var vscodePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create .vscode folder so the scanner detects VS Code environment
        Directory.CreateDirectory(vscodePath);

        // Accept agents before choosing independent assets and project scope.
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);
        await auto.EnterAsync(); // Accept default workspace path
        await auto.WaitUntilAsync(
            s => s.ContainsText("Which agents do you use?") &&
                s.ContainsText("GitHub Copilot") && s.ContainsText("OpenCode") && s.ContainsText("Claude Code"),
            timeout: TimeSpan.FromSeconds(30), description: "agent selection prompt");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which skills and tools do you want to configure?", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("aspire — Agent assets from microsoft/aspire-skills", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which source do you want to use for Aspire?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Configure the Aspire MCP server for selected agent environments?"),
            timeout: TimeSpan.FromSeconds(30), description: "MCP server confirmation prompt");
        // MCP configuration is strictly opt-in and defaults to No, so accepting the default
        // here leaves MCP unconfigured.
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Installation scope", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // The container creates root-owned private settings, so a non-root host cannot read them.
        await auto.RunCommandAsync(
            """test "$(stat -c '%a' "$ASPIRE_E2E_WORKSPACE/.github/copilot/settings.json")" = 600""", counter);
        await auto.RunCommandAsync(
            """jq -e '.enabledPlugins["aspire@aspire-skills"] == true and .extraKnownMarketplaces["aspire-skills"].source.repo == "microsoft/aspire-skills"' "$ASPIRE_E2E_WORKSPACE/.github/copilot/settings.json" """,
            counter);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills")));

        // Verify MCP was not configured, since it was never selected in the prompt above.
        var vscodeMcpConfigPath = Path.Combine(vscodePath, "mcp.json");
        Assert.False(File.Exists(vscodeMcpConfigPath), $"Expected no MCP config to be written but found {vscodeMcpConfigPath}");
    }

    /// <summary>
    /// Bundled mode includes manifest-defined skills without selecting independent tools.
    /// </summary>
    [Fact]
    public async Task AgentInit_NonInteractive_BundleOnlySkillsNotInCatalog()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAspireSkillsBundle(strategy);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        var bundleOnlySkills = new[] { "aspire-init", "aspire-monitoring", "aspire-orchestration" };

        await auto.TypeAsync("aspire agent init --workspace-root . --agents copilot --scope project --skills aspire --aspire-source embedded --mcp=false");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        var skillsRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills");
        foreach (var skillName in bundleOnlySkills)
        {
            var skillFile = Path.Combine(skillsRoot, skillName, "SKILL.md");
            Assert.True(File.Exists(skillFile), $"Expected {skillName} SKILL.md at {skillFile}");
            Assert.Contains($"name: {skillName}", File.ReadAllText(skillFile));
        }
    }

    /// <summary>
    /// Regression test for the chained agent init flow reached via <c>aspire init</c>: verifies that
    /// accepting agent init never surfaces the retired "Install Aspire MCP server" entry, either mixed
    /// into the skill selection list or as its own prompt. MCP configuration is only reachable through
    /// standalone <c>aspire agent init</c>, which chained flows never chain into.
    /// </summary>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireInit_ChainedAgentInit_NeverOffersMcpConfiguration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAspireSkillsBundle(strategy);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Provide editor detection evidence for the client defaults, but check Copilot's
        // canonical project MCP destination rather than its repair-only editor location.
        var vscodePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode");
        var mcpConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string originalMcpConfig = """{"mcpServers":{"custom":{"command":"custom"}}}""";

        // Pass --language so the interactive language prompt is skipped, then accept the chained
        // agent init prompt (instead of declining it) to reach the skills-source selection.
        await auto.TypeAsync("aspire init --language csharp --skills aspire");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created aspire.config.json", timeout: TimeSpan.FromMinutes(2));

        await auto.WaitUntilAsync(
            s => s.ContainsText("configure AI agent environments"),
            timeout: TimeSpan.FromSeconds(30),
            description: "agent init prompt after aspire init");
        Directory.CreateDirectory(vscodePath);
        await File.WriteAllTextAsync(mcpConfigPath, originalMcpConfig);
        await auto.WaitAsync(500);
        await auto.TypeAsync("y");

        await auto.WaitUntilAsync(
            s => s.ContainsText("Which agents do you use?") &&
                s.ContainsText("GitHub Copilot") && s.ContainsText("OpenCode") && s.ContainsText("Claude Code"),
            timeout: TimeSpan.FromSeconds(30), description: "agent selection prompt");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => s.ContainsText("Which source do you want to use for Aspire?"),
            timeout: TimeSpan.FromSeconds(60), description: "Aspire skills-source selection");
        var skillSelectionScreen = auto.CreateSnapshot().GetScreenText();
        Assert.Contains("Embedded (bundled skill files)", skillSelectionScreen);
        Assert.Contains("Remote (plugin)", skillSelectionScreen);
        Assert.Contains("at your own risk", skillSelectionScreen);
        Assert.Contains("https://github.com/microsoft/aspire-skills", skillSelectionScreen);
        Assert.DoesNotContain("Install Aspire MCP server", skillSelectionScreen);
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("Installation scope", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();

        // The chained flow never registers --mcp. It completes after client and scope selection
        // without an MCP prompt, leaving the selected project MCP configuration untouched.
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        var completionScreen = auto.CreateSnapshot().GetScreenText();
        Assert.DoesNotContain("Configure the Aspire MCP server", completionScreen);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.Equal(originalMcpConfig, await File.ReadAllTextAsync(mcpConfigPath));
    }

    /// <summary>
    /// Regression test for the chained agent init flow reached via <c>aspire new</c>: verifies that
    /// bundled mode includes <c>aspireify</c>, and MCP configuration remains unavailable.
    /// </summary>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireNew_ChainedAgentInit_BundledIncludesAspireifyAndNeverOffersMcp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        RequireCurrentAspireSkillsBundle(strategy);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Explicit Claude selection bypasses discovery. Seed its MCP destination inside the
        // selected project so any unintended chained MCP configuration changes this file.
        var mcpConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "StarterApp", ".mcp.json");
        const string originalMcpConfig = """{"mcpServers":{"custom":{"command":"custom"}}}""";

        // Explicit client, tool, and scope choices leave only the skills-source prompt.
        await auto.AspireNewAcceptingAgentInitAsync(
            "StarterApp",
            extraArguments: "--agents claude --scope project --skills aspire",
            beforeAcceptingAgentInit: () => File.WriteAllTextAsync(mcpConfigPath, originalMcpConfig));

        await auto.WaitUntilAsync(
            s => s.ContainsText("Which source do you want to use for Aspire?"),
            timeout: TimeSpan.FromSeconds(60), description: "Aspire skills-source selection");
        var skillSelectionScreen = auto.CreateSnapshot().GetScreenText();
        Assert.Contains("Embedded (bundled skill files)", skillSelectionScreen);
        Assert.DoesNotContain("Install Aspire MCP server", skillSelectionScreen);
        await auto.DownAsync();
        await auto.EnterAsync();

        // aspire new never registers --mcp, so it goes straight from skill selection to
        // "configuration complete" — no MCP prompt is ever shown, and Claude's selected
        // project MCP configuration is left untouched.
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        var completionScreen = auto.CreateSnapshot().GetScreenText();
        Assert.DoesNotContain("Configure the Aspire MCP server", completionScreen);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "StarterApp", ".claude", "skills", "aspireify", "SKILL.md")));
        Assert.Equal(originalMcpConfig, await File.ReadAllTextAsync(mcpConfigPath));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AgentInit_DefaultSource_RegistersAllClientsOfflineAndPreservesSettingsOnRepeat()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var project = workspace.CreateDirectory("project");
        var home = workspace.CreateDirectory("home");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var executablePathFile = Path.Combine(workspace.WorkspaceRoot.FullName, "cli-executable.txt");
        await auto.RunCommandAsync(
            $"readlink -f \"$(command -v aspire)\" > {AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(executablePathFile, workspace))}",
            counter);
        var executablePath = (await File.ReadAllTextAsync(executablePathFile, TestContext.Current.CancellationToken)).Trim();
        await auto.RunCommandAsync("docker network disconnect bridge \"$HOSTNAME\"", counter);
        var containerHome = CliE2ETestHelpers.ToContainerPath(home.FullName, workspace);
        await auto.RunCommandAsync(
            $"export HOME={AspireCliShellCommandHelpers.QuoteBashArg(containerHome)} " +
            $"XDG_CONFIG_HOME={AspireCliShellCommandHelpers.QuoteBashArg($"{containerHome}/.config")} " +
            $"COPILOT_HOME={AspireCliShellCommandHelpers.QuoteBashArg($"{containerHome}/.copilot")} " +
            $"CLAUDE_CONFIG_DIR={AspireCliShellCommandHelpers.QuoteBashArg($"{containerHome}/.claude")} " +
            "ASPIRE_CLI_TELEMETRY_OPTOUT=true", counter);
        var containerProject = CliE2ETestHelpers.ToContainerPath(project.FullName, workspace);
        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(containerProject)}", counter);

        const string pluginSettings = """{"theme":"dark","enabledPlugins":{"team@team":false}}""";
        const string expectedPluginSettings = """
            {
              "theme": "dark",
              "enabledPlugins": { "team@team": false, "aspire@aspire-skills": true },
              "extraKnownMarketplaces": {
                "aspire-skills": { "source": { "source": "github", "repo": "microsoft/aspire-skills" } }
              }
            }
            """;
        // Opt-out suppresses emission, but global setup still registers the CLI hook.
        var expectedClaudeGlobalSettings = JsonNode.Parse(expectedPluginSettings)!;
        expectedClaudeGlobalSettings["hooks"] = JsonNode.Parse($$"""
            {
              "PostToolUse": [{
                "matcher": "*",
                "hooks": [{
                  "type": "command",
                  "command": {{JsonSerializer.Serialize(executablePath)}},
                  "args": ["agent", "telemetry", "--hook"],
                  "timeout": 30
                }]
              }]
            }
            """);
        const string openCodeSettings = """
            {"theme":"dark","mcp":{"custom":{"type":"remote","url":"https://example.test/mcp"}},"skills":{"paths":["./team-skills"],"urls":["https://example.test/skills/"]}}
            """;
        const string expectedOpenCodeSettings = """
            {
              "theme": "dark",
              "mcp": { "custom": { "type": "remote", "url": "https://example.test/mcp" } },
              "skills": {
                "paths": ["./team-skills"],
                "urls": ["https://example.test/skills/", "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/"]
              }
            }
            """;

        foreach (var scope in new[] { "project", "global" })
        {
            var root = scope == "project" ? project.FullName : home.FullName;
            var copilotDirectory = scope == "project" ? Path.Combine(root, ".github", "copilot") : Path.Combine(root, ".copilot");
            (string Path, string Original, string Expected)[] files =
            [
                (Path.Combine(copilotDirectory, "settings.json"), pluginSettings, expectedPluginSettings),
                (Path.Combine(root, ".claude", "settings.json"), pluginSettings,
                    scope == "global" ? expectedClaudeGlobalSettings.ToJsonString() : expectedPluginSettings),
                (Path.Combine(scope == "project" ? root : Path.Combine(root, ".config", "opencode"), "opencode.jsonc"),
                    openCodeSettings, expectedOpenCodeSettings)
            ];
            foreach (var (path, original, _) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
            }

            var command = $"aspire agent init --workspace-root . --agents all --scope {scope} --skills aspire --mcp=false --non-interactive";
            await auto.RunCommandAsync(command, counter, TimeSpan.FromSeconds(60));
            var configured = new Dictionary<string, byte[]>();
            foreach (var (path, _, expected) in files)
            {
                var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(bytes)),
                    $"Unexpected remote registration in {path}: {System.Text.Encoding.UTF8.GetString(bytes)}");
                configured.Add(path, bytes);
            }

            await auto.RunCommandAsync(command, counter, TimeSpan.FromSeconds(60));
            foreach (var (path, bytes) in configured)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            }
        }
    }

    private static void RequireCurrentAspireSkillsBundle(CliInstallStrategy strategy)
    {
        Assert.SkipWhen(
            strategy.Mode == CliInstallMode.InstallScript ||
            (strategy.Mode == CliInstallMode.DotnetTool && strategy.NupkgSourcePath is null),
            "This test validates the current Aspire CLI's embedded skills bundle. Use a local or PR CLI build instead of a released CLI.");
    }
}
