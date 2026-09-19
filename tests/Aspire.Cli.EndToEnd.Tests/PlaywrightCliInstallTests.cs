// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test verifying that the Playwright CLI installation flow works correctly
/// through <c>aspire agent init</c>, including npm provenance verification and skill file generation.
/// </summary>
[OuterloopTest("Requires npm and network access to install @playwright/cli from the npm registry")]
public sealed class PlaywrightCliInstallTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies the full Playwright CLI installation lifecycle:
    /// 1. Playwright CLI is not initially installed
    /// 2. An Aspire project is created
    /// 3. <c>aspire agent init</c> is run with Claude Code explicitly selected
    /// 4. Playwright CLI is installed and available on PATH
    /// 5. The generated skill is installed at both native project and user locations
    /// </summary>
    [Fact]
    public async Task AgentInit_InstallsPlaywrightCli_AndGeneratesSkillFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipWhen(
            strategy.Mode == CliInstallMode.InstallScript ||
            (strategy.Mode == CliInstallMode.DotnetTool && strategy.NupkgSourcePath is null),
            "This test requires the current CLI's independent agent asset options. Use a local or PR CLI build.");
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export CLAUDE_CONFIG_DIR=\"$PWD/.client-config/claude\"", counter);

        // Step 1: Verify playwright-cli is not installed.
        await auto.TypeAsync("playwright-cli --version 2>&1 || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Create an Aspire project (accept all defaults).
        await auto.AspireNewAsync("TestProject", counter);

        // Explicit client selection does not require a native client or configuration to exist.
        await auto.TypeAsync("cd TestProject");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Run aspire agent init for Playwright only. This test is about
        // @playwright/cli acquisition, not native Aspire source registration.
        await auto.TypeAsync("aspire agent init --workspace-root . --clients claude-code --playwright y --dotnet-inspect n --aspire-skills n --mcp n --non-interactive");
        await auto.EnterAsync();

        // Wait for installation to complete (this downloads from npm, can take a while)
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Verify playwright-cli is now installed.
        await auto.TypeAsync("playwright-cli --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Both scopes receive the same complete payload, including supporting files.
        await auto.TypeAsync("ls .claude/skills/playwright-cli/SKILL.md \"$CLAUDE_CONFIG_DIR/skills/playwright-cli/SKILL.md\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.RunCommandAsync("diff -r .claude/skills/playwright-cli \"$CLAUDE_CONFIG_DIR/skills/playwright-cli\"", counter);
        await auto.RunCommandAsync("test ! -e .agents/skills/playwright-cli && test ! -e .github/skills/playwright-cli", counter);
    }

    /// <summary>
    /// Verifies that when <c>aspire agent init</c> is run from a different directory than the
    /// workspace root, the isolated Playwright generation is installed in the workspace
    /// root and selected user directory, not the current working directory.
    ///
    /// This is a regression test for https://github.com/microsoft/aspire/issues/15140 where
    /// the missing <c>WorkingDirectory</c> on <c>ProcessStartInfo</c> caused skill files
    /// to be dropped in the CLI process's current working directory.
    /// </summary>
    [Fact]
    public async Task AgentInit_CwdDiffersFromRoot_PlacesSkillsInWorkspaceRoot()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipWhen(
            strategy.Mode == CliInstallMode.InstallScript ||
            (strategy.Mode == CliInstallMode.DotnetTool && strategy.NupkgSourcePath is null),
            "This test requires the current CLI's independent agent asset options. Use a local or PR CLI build.");
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("export CLAUDE_CONFIG_DIR=\"$PWD/.client-config/claude\"", counter);

        // Step 1: Create an Aspire project.
        await auto.AspireNewAsync("TestProject", counter);

        // Stay in the parent directory. The explicit workspace root and client selection
        // must control project destinations independently of generation's working directory.
        await auto.TypeAsync("aspire agent init --workspace-root TestProject --clients claude-code --playwright y --dotnet-inspect n --aspire-skills n --mcp n --non-interactive");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the project and user targets both contain the complete skill.
        await auto.TypeAsync("ls TestProject/.claude/skills/playwright-cli/SKILL.md \"$CLAUDE_CONFIG_DIR/skills/playwright-cli/SKILL.md\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.RunCommandAsync("diff -r TestProject/.claude/skills/playwright-cli \"$CLAUDE_CONFIG_DIR/skills/playwright-cli\"", counter);

        await auto.RunCommandAsync("test ! -e .claude/skills/playwright-cli && test ! -e .agents/skills/playwright-cli", counter);
    }
}
