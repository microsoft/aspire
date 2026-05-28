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

        // Step 2: Run aspire agent init - should detect and auto-migrate deprecated config.
        // Skill installation is not part of this migration coverage, so keep it disabled
        // to avoid depending on the external Aspire skills package.
        await auto.TypeAsync("aspire agent init --workspace-root . --skill-locations none --skills none");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptFailFastAsync(counter);

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
    /// Tests that aspire agent init with a .vscode folder shows skill location and skill selection
    /// prompts, and that accepting the defaults completes successfully and creates the default
    /// skill files in the .agents/skills/ directory.
    /// </summary>
    [Fact]
    public async Task AgentInitCommand_DefaultSelection_InstallsDefaultSkills()
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
        await SeedAspireSkillsBundleCacheAsync(auto, workspace, counter);

        // Run aspire agent init and accept the default location and skills. The cache
        // fixture above keeps this independent from the unpublished npm package.
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);
        await auto.EnterAsync(); // Accept default workspace path
        await auto.WaitUntilAsync(
            s => s.ContainsText("skill files be installed"),
            timeout: TimeSpan.FromSeconds(60), description: "skill location prompt");
        await auto.EnterAsync(); // Accept default skill locations (Standard pre-selected)
        await auto.WaitUntilAsync(
            s => s.ContainsText("skills should be installed"),
            timeout: TimeSpan.FromSeconds(30), description: "skill selection prompt");
        // Playwright and dotnet-inspect are not pre-selected, so just accept
        // the default Aspire skills from the seeded bundle.
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptFailFastAsync(counter);

        // Verify skill files were created (skills are now installed at .agents/skills/ by StandardLocationAgentEnvironmentScanner)
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md");
        var fileContent = File.ReadAllText(skillFilePath);
        Assert.Contains("aspire start", fileContent);
        var deploymentSkillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire-deployment", "SKILL.md");
        var deploymentFileContent = File.ReadAllText(deploymentSkillFilePath);
        Assert.Contains("Aspire Deployment", deploymentFileContent);
    }

    private static async Task SeedAspireSkillsBundleCacheAsync(Hex1bTerminalAutomator auto, TemporaryWorkspace workspace, SequenceCounter counter)
    {
        const string aspireSkillsVersion = "0.0.1";
        var scriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "seed-aspire-skills-cache.sh");
        var script =
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            cache="$HOME/.aspire/cache/aspire-skills/{{aspireSkillsVersion}}"
            rm -rf "$cache"
            mkdir -p \
              "$cache/skills/aspire/references" \
              "$cache/skills/aspire/evals" \
              "$cache/skills/aspireify" \
              "$cache/skills/aspire-deployment/references"

            cat > "$cache/skills/aspire/SKILL.md" <<'SKILL'
            ---
            name: aspire
            description: "Aspire CLI commands and workflows for distributed apps"
            ---

            # Aspire Skill

            Use `aspire start` to start an Aspire app.
            SKILL
            printf '%s\n' '# App commands' > "$cache/skills/aspire/references/app-commands.md"
            printf '%s\n' '{}' > "$cache/skills/aspire/evals/evals.json"

            cat > "$cache/skills/aspireify/SKILL.md" <<'SKILL'
            ---
            name: aspireify
            description: "One-time setup: wire up AppHost with discovered projects"
            ---

            # Aspireify
            SKILL

            cat > "$cache/skills/aspire-deployment/SKILL.md" <<'SKILL'
            ---
            name: aspire-deployment
            description: "Aspire deployment target selection, preflight, publish, and deploy workflows"
            ---

            # Aspire Deployment
            SKILL
            printf '%s\n' '# Preflight' > "$cache/skills/aspire-deployment/references/preflight.md"

            aspire_skill_hash="$(sha256sum "$cache/skills/aspire/SKILL.md" | awk '{print $1}')"
            aspire_commands_hash="$(sha256sum "$cache/skills/aspire/references/app-commands.md" | awk '{print $1}')"
            aspire_evals_hash="$(sha256sum "$cache/skills/aspire/evals/evals.json" | awk '{print $1}')"
            aspireify_skill_hash="$(sha256sum "$cache/skills/aspireify/SKILL.md" | awk '{print $1}')"
            deployment_skill_hash="$(sha256sum "$cache/skills/aspire-deployment/SKILL.md" | awk '{print $1}')"
            deployment_preflight_hash="$(sha256sum "$cache/skills/aspire-deployment/references/preflight.md" | awk '{print $1}')"

            cat > "$cache/skill-manifest.json" <<JSON
            {
              "version": "{{aspireSkillsVersion}}",
              "supports": {
                "aspireCli": ">=0.0.0 <999.0.0",
                "aspireSdk": ">=0.0.0 <999.0.0"
              },
              "skills": [
                {
                  "name": "aspire",
                  "description": "Aspire CLI commands and workflows for distributed apps",
                  "isDefault": true,
                  "installExcludedRelativePaths": ["evals"],
                  "files": [
                    { "relativePath": "SKILL.md", "sha256": "$aspire_skill_hash" },
                    { "relativePath": "references/app-commands.md", "sha256": "$aspire_commands_hash" },
                    { "relativePath": "evals/evals.json", "sha256": "$aspire_evals_hash" }
                  ]
                },
                {
                  "name": "aspireify",
                  "description": "One-time setup: wire up AppHost with discovered projects",
                  "isDefault": true,
                  "files": [
                    { "relativePath": "SKILL.md", "sha256": "$aspireify_skill_hash" }
                  ]
                },
                {
                  "name": "aspire-deployment",
                  "description": "Aspire deployment target selection, preflight, publish, and deploy workflows",
                  "isDefault": true,
                  "files": [
                    { "relativePath": "SKILL.md", "sha256": "$deployment_skill_hash" },
                    { "relativePath": "references/preflight.md", "sha256": "$deployment_preflight_hash" }
                  ]
                }
              ]
            }
            JSON
            """;

        await File.WriteAllTextAsync(scriptPath, script.ReplaceLineEndings("\n"));

        var containerScriptPath = CliE2ETestHelpers.ToContainerPath(scriptPath, workspace);
        await auto.RunCommandAsync($"bash {AspireCliShellCommandHelpers.QuoteBashArg(containerScriptPath)}", counter, TimeSpan.FromSeconds(30));
        await auto.RunCommandAsync($"export aspireSkillsVersion={AspireCliShellCommandHelpers.QuoteBashArg(aspireSkillsVersion)}", counter, TimeSpan.FromSeconds(30));
    }
}
