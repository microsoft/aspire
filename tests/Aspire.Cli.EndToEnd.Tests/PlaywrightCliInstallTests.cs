// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
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
    /// 3. <c>aspire agent init</c> is run with Claude Code environment selected
    /// 4. Playwright CLI is installed and available on PATH
    /// 5. The <c>.claude/skills/playwright-cli/SKILL.md</c> skill file is generated
    /// </summary>
    [Fact]
    public async Task AgentInit_InstallsPlaywrightCli_AndGeneratesSkillFiles()
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

        // Step 1: Verify playwright-cli is not installed.
        await auto.TypeAsync("playwright-cli --version 2>&1 || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Create an Aspire project (accept all defaults).
        await auto.AspireNewAsync("TestProject", counter);

        // Step 3: Navigate into the project and create .claude folder to trigger Claude Code detection.
        await auto.TypeAsync("cd TestProject && mkdir -p .claude");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Run aspire agent init for Playwright only. This test is about
        // @playwright/cli acquisition, not the Aspire skills bundle.
        await auto.TypeAsync("aspire agent init --workspace-root . --skill-locations claudecode --skills playwright-cli");
        await auto.EnterAsync();

        // Wait for installation to complete (this downloads from npm, can take a while)
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Verify playwright-cli is now installed.
        await auto.TypeAsync("playwright-cli --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 6: Verify the skill file was generated.
        await auto.TypeAsync("ls .claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    /// <summary>
    /// Verifies that when <c>aspire agent init</c> is run from a different directory than the
    /// workspace root, <c>playwright-cli install --skills</c> generates skill files in the
    /// workspace root, not the current working directory.
    ///
    /// This is a regression test for https://github.com/microsoft/aspire/issues/15140 where
    /// the missing <c>WorkingDirectory</c> on <c>ProcessStartInfo</c> caused skill files
    /// to be dropped in the CLI process's current working directory.
    /// </summary>
    [Fact]
    public async Task AgentInit_WhenCwdDiffersFromWorkspaceRoot_PlacesSkillFilesInWorkspaceRoot()
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

        // Step 1: Create an Aspire project.
        await auto.AspireNewAsync("TestProject", counter);

        // Step 2: Create .claude folder inside the project to trigger Claude Code detection.
        // Crucially, do NOT cd into the project — stay in the parent directory.
        await auto.TypeAsync("mkdir -p TestProject/.claude");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Run aspire agent init from the PARENT directory for Playwright
        // only. When provided as options, the workspace root and skill selection
        // are deterministic and do not depend on Aspire default skills.
        await auto.TypeAsync("aspire agent init --workspace-root TestProject --skill-locations claudecode --skills playwright-cli");
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Verify skill file exists in the workspace root (project subdirectory).
        await auto.TypeAsync("ls TestProject/.claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Verify no stray skill files were created in the CWD (parent directory).
        await auto.TypeAsync("test -d .claude/skills/playwright-cli && echo 'STRAY_FILES_FOUND' || echo 'NO_STRAY_FILES'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("NO_STRAY_FILES", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
