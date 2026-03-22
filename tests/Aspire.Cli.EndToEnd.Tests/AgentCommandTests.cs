// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
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
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

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

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Tests that deprecated MCP configs are detected and can be migrated
    /// to the new agent mcp format during aspire agent init.
    /// </summary>
    [Fact]
    public async Task AgentInitCommand_MigratesDeprecatedConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        // Use .mcp.json (Claude Code format) for simpler testing
        // This is the same format used by the doctor test that passes
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        var containerConfigPath = CliE2ETestHelpers.ToContainerPath(configPath, workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

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

        // Step 2: Run aspire agent init - should detect and auto-migrate deprecated config
        // In the new flow, deprecated config migrations are applied silently
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500); // Small delay to ensure prompt is ready
        await auto.EnterAsync(); // Accept default workspace path
        await auto.WaitUntilAsync(
            s => s.ContainsText("configure") || s.ContainsText("No agent environments") || s.ContainsText("omplete"),
            timeout: TimeSpan.FromSeconds(60), description: "configure prompt, completion, or no environments message");

        // If we got the configure prompt, just press Enter to accept defaults
        // If we got complete/no-env, this Enter is harmless
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Debug: Show the scanner log file to diagnose what the scanner found
        await auto.TypeAsync("cat /tmp/aspire-deprecated-scan.log 2>/dev/null || echo 'No debug log found'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Scanning context", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Verify config was updated to new format
        // The updated config should contain "agent" and "mcp" but not "start"
        fileContent = File.ReadAllText(configPath);
        Assert.Contains("\"agent\"", fileContent);
        Assert.Contains("\"mcp\"", fileContent);
        Assert.DoesNotContain("\"start\"", fileContent);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Tests that aspire doctor warns about deprecated agent configs.
    /// </summary>
    [Fact]
    public async Task DoctorCommand_DetectsDeprecatedAgentConfig()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Create deprecated config file
        File.WriteAllText(configPath, """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""");
        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("dev-certs") && s.ContainsText("deprecated") && s.ContainsText("aspire agent init"),
            timeout: TimeSpan.FromSeconds(60), description: "doctor output with deprecated warning and fix suggestion");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Tests that aspire agent init with a .vscode folder shows the skill pre-selected
    /// and MCP as an opt-in option, and that accepting the defaults (skill only) completes
    /// successfully and creates the skill file.
    /// </summary>
    [Fact]
    public async Task AgentInitCommand_DefaultSelection_InstallsSkillOnly()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        // Set up .vscode folder so VS Code scanner detects it
        var vscodePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Create .vscode folder so the scanner detects VS Code environment
        Directory.CreateDirectory(vscodePath);

        // Run aspire agent init and accept defaults (skill is pre-selected)
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);
        await auto.EnterAsync(); // Accept default workspace path
        await auto.WaitUntilAsync(
            s => s.ContainsText("configure") && s.ContainsText("skill"),
            timeout: TimeSpan.FromSeconds(60), description: "configure prompt with skill option");
        await auto.EnterAsync(); // Accept defaults (skill pre-selected)
        await auto.WaitUntilTextAsync("complete", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify skill file was created
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "skills", "aspire", "SKILL.md");
        var fileContent = File.ReadAllText(skillFilePath);
        Assert.Contains("aspire start", fileContent);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}

